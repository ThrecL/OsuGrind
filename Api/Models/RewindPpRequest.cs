using System.Collections.Generic;

namespace OsuGrind.Api.Models
{
    public class RewindPpRequest
    {
        public string BeatmapPath { get; set; } = string.Empty;
        public string BeatmapHash { get; set; } = string.Empty;
        public List<string> Mods { get; set; } = new();
        public int Combo { get; set; }
        public int Count300 { get; set; }
        public int Count100 { get; set; }
        public int Count50 { get; set; }
        public int Misses { get; set; }
        public int PassedObjects { get; set; }
        public int SliderEndHits { get; set; }
        public int LargeTickHits { get; set; }
        public int SmallTickHits { get; set; }
    }
}
