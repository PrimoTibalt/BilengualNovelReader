namespace NovelReader.Domain.RealTimeReader.Parsing
{
    public class NovelDataDto
    {
        public string Title { get; set; }
        public string Slug { get; set; }
        public int? Rank { get; set; }
        public int? TotalChapter { get; set; }

        public NovelDataDto(string title, string slug, int? rank, int? totalChapter)
        {
            Title = title;
            Slug = slug;
            Rank = rank;
            TotalChapter = totalChapter;
        }
    }
}
