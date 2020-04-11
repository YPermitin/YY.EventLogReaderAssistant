﻿using System.ComponentModel.DataAnnotations;

namespace YY.EventLogAssistant.Models
{
    public class Computers
    {
        [Key]
        public long Code { get; set; }
        [MaxLength(250)]
        public string Name { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}