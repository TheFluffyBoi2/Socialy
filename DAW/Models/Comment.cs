﻿using System.ComponentModel.DataAnnotations;

namespace DAW.Models
{
    public class Comment
    {
        [Key]
        public int Id { get; set; }
        [Required(ErrorMessage = "You can't add an empty comment")]
        public string? Content { get; set; }
        public DateTime Date { get; set; }
        public string? UserId { get; set; }
        public int? PostId { get; set; }
        public virtual Post? Post { get; set; }
        public virtual ApplicationUser? User { get; set; }
    }
}
