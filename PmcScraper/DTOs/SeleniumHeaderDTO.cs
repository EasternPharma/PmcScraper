using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PmcScraper.DTOs;

public class SeleniumHeaderDTO
{
    public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    public Dictionary<string, string> Cookies { get; set; } = new Dictionary<string, string>();
}
