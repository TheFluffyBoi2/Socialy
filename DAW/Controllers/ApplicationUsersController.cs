using DAW.Data;
using DAW.Data.Migrations;
using DAW.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace DAW.Controllers
{
    public class ApplicationUsersController : Controller
    {
        private readonly ApplicationDbContext db;
        private readonly IWebHostEnvironment _env;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        public ApplicationUsersController(
            ApplicationDbContext context,
            IWebHostEnvironment env,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager)
        {
            db = context;
            _env = env;
            _userManager = userManager;
            _roleManager = roleManager;
        }

        [Authorize(Roles = "Admin, User")]
        public IActionResult Index()
        {
            var search = "";
            if (Convert.ToString(HttpContext.Request.Query["search"]) != null)
            {
                search = Convert.ToString(HttpContext.Request.Query["search"]).Trim();
            }

            if (search == "")
            {
                ViewBag.SearchString = search;
                ViewBag.Users = null;
                return View();
            }
            int length = search.Length;

            List<string> usersId = db.Users.Where(u => u.FirstName.Contains(search) || u.LastName.Contains(search) || (u.LastName + ' ' + u.FirstName).Contains(search) || (u.FirstName + ' ' + u.LastName).Contains(search))
                                .OrderByDescending(u => u.UserName).Select(u => u.Id).ToList();
            ViewBag.SearchString = search;
            ViewBag.Users = db.Users.Include(u => u.Posts).Where(u => usersId.Contains(u.Id)).OrderByDescending(u => u.UserName);
            return View();
        }

        
        [Authorize(Roles="Admin")]
        public IActionResult ShowAll()
        {
            ViewBag.Users = db.Users.Include("Posts").OrderByDescending(u => u.UserName);
            return View();
        }

        [Authorize(Roles = "Admin, User")]
        public IActionResult Show(string id)
        {
            ApplicationUser user = db.Users.Include(p => p.Posts).ThenInclude(c => c.Comments).ThenInclude(u => u.User)
              .Where(_user => _user.Id == id).First();
            var posts = db.Posts.Include(p => p.User).Include(p => p.Comments).Where(p => p.UserId == user.Id).OrderByDescending(p => p.Date);
            int _perPage = 5;
            var offset = 0;
            var totalItems = posts.Where(p => !(p is GroupPost)).Count();
            var currentPage = Convert.ToInt32(HttpContext.Request.Query["page"]);
            if (!currentPage.Equals(0))
            {
                offset = (currentPage - 1) * _perPage;
            }
            var paginatedPosts = posts.Skip(offset).Take(_perPage);
            ViewBag.LastPage = Math.Ceiling((float)totalItems / (float)_perPage);
            ViewBag.Posts = paginatedPosts;
            ViewBag.PaginationBaseUrl = "/ApplicationUsers/Show/" + user.Id + "?page";
            ViewBag.Message = TempData["message"];
            ViewBag.Alert = TempData["messageType"];
            AlreadySent(id);
            AlreadyFriends(id);
            IsAdmin();
            CurrentUser();
            ViewBag.HasAccess = HasAccess(user);

            //
            ViewBag.FriendCount = GetFriendCount(id);

            if (IsBlocked(id))
            {
                return Forbid();
            }
            return View(user);
        }

        [Authorize(Roles = "Admin, User")]
        [HttpPost]
        public async Task<IActionResult> Show([FromForm] Post post, IFormFile image)
        {

            post.Date = DateTime.Now;
            post.UserId = _userManager.GetUserId(User);
            post.Likes = 0;
            post.Dislikes = 0;

            if (image != null && image.Length > 0)
            {
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif",
".mp4", ".mov" };
                var fileExtension = Path.GetExtension(image.FileName).ToLower();
                if (!allowedExtensions.Contains(fileExtension))
                {
                    ModelState.AddModelError("Image", "The file must be an image (jpg, jpeg, png, gif) or a video (mp4, mov).");
                    TempData["message"] = ModelState.Values
                                     .SelectMany(v => v.Errors)
                                     .Select(e => e.ErrorMessage)
                                     .FirstOrDefault();
                    TempData["messageType"] = "alert-danger";
                    return RedirectToAction("Show", "ApplicationUsers", post.UserId);
                }

                var storagePath = Path.Combine(_env.WebRootPath, "images",
                image.FileName);
                var databaseFileName = "/images/" + image.FileName;

                using (var fileStream = new FileStream(storagePath, FileMode.Create))
                {
                    await image.CopyToAsync(fileStream);
                }

                ModelState.Remove(nameof(post.Image));
                post.Image = databaseFileName;
            }
            else
            {
                ModelState.Remove("Image");
            }

            if (post.Video != null && post.Video.Length > 0)
            {
                if (ExtractVideoId(post.Video) == string.Empty)
                {
                    ModelState.AddModelError("Video", "The URL must be from YouTube.");
                    TempData["message"] = ModelState.Values
                                     .SelectMany(v => v.Errors)
                                     .Select(e => e.ErrorMessage)
                                     .FirstOrDefault();
                    TempData["messageType"] = "alert-danger";
                    return RedirectToAction("Show", "ApplicationUsers", post.UserId);
                }
                post.Video = "https://youtube.com/embed/"+ExtractVideoId(post.Video);
            }
            else
            {
                ModelState.Remove("Video");
            }


            if (ModelState.IsValid)
            {
                db.Posts.Add(post);
                db.SaveChanges();
                TempData["message"] = "Post added";
                TempData["messageType"] = "alert-success";
                return RedirectToAction("Show", "ApplicationUsers", post.UserId);
            }
            else
            {
                TempData["message"] = ModelState.Values
                                 .SelectMany(v => v.Errors)
                                 .Select(e => e.ErrorMessage)
                                 .FirstOrDefault();
                TempData["messageType"] = "alert-danger";
                return RedirectToAction("Show", "ApplicationUsers", post.UserId);
            }
        }

        [Authorize(Roles = "Admin, User")]
        public IActionResult Edit(string id)
        {
            ApplicationUser user = db.Users.Include("Posts")
                .Where(_user => _user.Id == id).First();
            IsAdmin();

            if (HasAccess(user))
            {
                return View(user);
            }
            else
            {
                return RedirectToAction("Show", "ApplicationUsers", new { id = user.Id });
            }
        }

        [Authorize(Roles = "Admin, User")]
        [HttpPost]
        public async Task<IActionResult> Edit(string id, ApplicationUser requestUser, IFormFile profilePicture)
        {
            ApplicationUser user = db.Users.Find(id);
            if (profilePicture != null && profilePicture.Length > 0)
            {
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif",
".mp4", ".mov" };
                var fileExtension = Path.GetExtension(profilePicture.FileName).ToLower();
                if (!allowedExtensions.Contains(fileExtension))
                {
                    ModelState.AddModelError("ProfilePicture", "The file must be an image (jpg, jpeg, png, gif) or a video (mp4, mov).");
                    return View(requestUser);
                }

                var storagePath = Path.Combine(_env.WebRootPath, "images",
                profilePicture.FileName);
                var databaseFileName = "/images/" + profilePicture.FileName;

                using (var fileStream = new FileStream(storagePath, FileMode.Create))
                {
                    await profilePicture.CopyToAsync(fileStream);
                }

                ModelState.Remove(nameof(user.ProfilePicture));
                user.ProfilePicture = databaseFileName;
            }
            else
            {
                ModelState.Remove("ProfilePicture");
            }

            user.FirstName = requestUser.FirstName;
            user.LastName = requestUser.LastName;
            user.Bio = requestUser.Bio;
            user.IsPublic = requestUser.IsPublic;

            if (TryValidateModel(user))
            {
                db.SaveChanges();
                return RedirectToAction("Show", "ApplicationUsers", new { id = user.Id });
            }
            else
            {
                return View(requestUser);
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public IActionResult Delete(string id)
        {
            System.Diagnostics.Debug.WriteLine("-------");
            System.Diagnostics.Debug.WriteLine(id + " " + "test");
            System.Diagnostics.Debug.WriteLine("--------");
            ApplicationUser? user = db.Users.Include("Posts").Where(u => u.Id == id).First();
            db.Users.Remove(user);
            db.SaveChanges();
            TempData["message"] = "User deleted";
            TempData["messageType"] = "alert-success";
            return Redirect("/Home/Index");
        }

        [Authorize(Roles = "Admin, User")]
        [HttpPost]
        public IActionResult Add(string id)
        {
            FriendRequest rq = new();
            rq.Date = DateTime.Now;
            rq.UserIdSender = _userManager.GetUserId(User);
            rq.UserIdReceiver = id;
            db.FriendRequests.Add(rq);
            db.SaveChanges();
            TempData["message"] = "Request sent";
            TempData["messageType"] = "alert-success";
            ViewBag.Clicked = true;
            return RedirectToAction("Show", "ApplicationUsers", new { id = rq.UserIdReceiver });
        }

        [Authorize(Roles="Admin, User")]
        [HttpPost]
        public IActionResult Block(string id)
        {
            UserRelationships ur = new();
            ur.Relation = "Blocked";
            ur.UserId1 = _userManager.GetUserId(User);
            ur.UserId2 = id;
            db.UserRelationships.Add(ur);
            TempData["message"] = "Blocked";
            TempData["messageType"] = "alert-success";
            db.SaveChanges();
            return RedirectToAction("Show", "ApplicationUsers", new { id = ur.UserId2 });
        }

        [Authorize(Roles = "Admin, User")]
        public IActionResult Friends(string id)
        {
            ViewBag.Friends = db.UserRelationships.Where(r => (r.UserId1 == id || r.UserId2 == id)
                                                        && r.Relation == "Friends").Include(r => r.User1)
                                                        .Include(r => r.User2);
            ApplicationUser user = db.Users.Find(id);
            CurrentUser();
            return View(user);
        }

        [Authorize(Roles = "Admin, User")]
        [HttpPost]
        public IActionResult RemoveFriend(string modelUserId, string otherUserId)
        {
            UserRelationships? ur = db.UserRelationships.Where(ur => ((ur.UserId1 == modelUserId && ur.UserId2 == otherUserId)
                                                                || (ur.UserId1 == otherUserId && ur.UserId2 == modelUserId)) && ur.Relation == "Friends")
                                                                .FirstOrDefault();
            if (ur != null)
            {
                db.UserRelationships.Remove(ur);
                db.SaveChanges();
            }
            return RedirectToAction("Friends", "ApplicationUsers", new { id = modelUserId });
        }

        [Authorize(Roles = "Admin, User")]
        [HttpPost]
        public IActionResult Unblock(string id)
        {
            UserRelationships? ur = db.UserRelationships.Where(ur => ur.UserId1 == _userManager.GetUserId(User) && ur.UserId2 == id)
                                                        .FirstOrDefault();
            if (ur != null)
            {
                TempData["message"] = "Unblocked";
                TempData["messageType"] = "alert-success";
                db.UserRelationships.Remove(ur);
                db.SaveChanges();
            }
            return RedirectToAction("Show", "ApplicationUsers", new { id = id });
        }

        [Authorize(Roles = "Admin, User")]
        [HttpPost]
        public IActionResult Like(int id)
        {
            Post? p = db.Posts.Where(p => p.Id == id).FirstOrDefault();
            if (p != null)
            {
                p.LikedBy.Add(_userManager.GetUserId(User));
                p.Likes++;
                db.SaveChanges();
            }
            return RedirectToAction("Show", "ApplicationUsers", new { id = p.UserId });
        }

        [Authorize(Roles = "Admin, User")]
        [HttpPost]
        public IActionResult Dislike(int id)
        {
            Post? p = db.Posts.Where(p => p.Id == id).FirstOrDefault();
            if (p != null)
            {
                p.DislikedBy.Add(_userManager.GetUserId(User));
                p.Dislikes++;
                db.SaveChanges();
            }

            return RedirectToAction("Show", "ApplicationUsers", new { id = p.UserId });
        }

        [NonAction]
        public bool HasAccess(ApplicationUser user)
        {
            return user.Id == _userManager.GetUserId(User) || User.IsInRole("Admin");
        }

        [NonAction]
        public void AlreadySent(string id)
        {
            FriendRequest? rq = db.FriendRequests.Where(r => r.UserIdSender == _userManager.GetUserId(User) && r.UserIdReceiver == id).FirstOrDefault();
            if (rq != null)
            {
                ViewBag.Clicked = true;
            }
        }

        [NonAction]
        public void AlreadyFriends(string id)
        {
            UserRelationships? ur = db.UserRelationships.Where(r => (r.UserId1 == id && r.UserId2 == _userManager.GetUserId(User))
                                                                    || (r.UserId2 == id && r.UserId1 == _userManager.GetUserId(User))
                                                                    && r.Relation == "Friends")
                                                                    .FirstOrDefault();
            if (ur != null)
            {
                ViewBag.AlreadyFriends = true;
            }
        }

        [NonAction]
        public string ExtractVideoId(string videoUrl)
        {
            if (string.IsNullOrWhiteSpace(videoUrl))
                return string.Empty;

            if (videoUrl.Contains("youtu.be/"))
            {
                int startIndex = videoUrl.IndexOf("youtu.be/") + "youtu.be/".Length;
                return videoUrl.Substring(startIndex, 11); 
            }

            if (videoUrl.Contains("v="))
            {
                int startIndex = videoUrl.IndexOf("v=") + "v=".Length;
                string id = videoUrl.Substring(startIndex);
                int ampersandIndex = id.IndexOf("&");
                if (ampersandIndex > -1)
                {
                    id = id.Substring(0, ampersandIndex); 
                }
                return id;
            }

            return string.Empty;
         }

        [NonAction]
        public bool IsBlocked(string id)
        {
            UserRelationships? ur = db.UserRelationships.Where(r => (r.UserId2 == _userManager.GetUserId(User) && r.UserId1 == id) && r.Relation == "Blocked").FirstOrDefault();
            if (ur != null)
            {
                return true;
            }
            UserRelationships? ur2 = db.UserRelationships.Where(r => (r.UserId1 == _userManager.GetUserId(User) && r.UserId2 == id) && r.Relation == "Blocked").FirstOrDefault();
            if (ur2 != null)
            {
                ViewBag.Blocked = true;
            }
            return false;

        }

        [NonAction]
        public void IsAdmin()
        {
            ViewBag.IsAdmin = false;
            if (User.IsInRole("Admin"))
            {
                ViewBag.IsAdmin = true;
            }
        }

        [NonAction]
        public void CurrentUser()
        {
            ViewBag.CurrentUser = _userManager.GetUserId(User);
        }

        [NonAction]
        public int GetFriendCount(string id)
        {
            var friendCount = db.UserRelationships
                .Where(r => r.Relation == "Friends" && (r.UserId1 == id || r.UserId2 == id))
                .Count();

            return friendCount;
        }
    }
}
