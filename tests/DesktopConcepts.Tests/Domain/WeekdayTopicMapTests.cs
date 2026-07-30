using DesktopConcepts.Domain;

namespace DesktopConcepts.Tests.Domain;

public sealed class WeekdayTopicMapTests
{
    private static readonly WeekdayTopicMap DefaultMap =
        AppSettings.Default().Topics;

    [Theory]
    [InlineData(DayOfWeek.Monday,    "Programming")]
    [InlineData(DayOfWeek.Tuesday,   "Cybersecurity")]
    [InlineData(DayOfWeek.Wednesday, "Networking")]
    [InlineData(DayOfWeek.Thursday,  "AI")]
    [InlineData(DayOfWeek.Friday,    "Operating Systems")]
    [InlineData(DayOfWeek.Saturday,  "Mathematics")]
    [InlineData(DayOfWeek.Sunday,    "Computer Engineering")]
    public void DefaultMap_ReturnsCorrectCategory(DayOfWeek day, string expected)
    {
        Assert.Equal(expected, DefaultMap.CategoryFor(day));
    }

    [Fact]
    public void MissingDay_ThrowsInvalidOperationException()
    {
        var emptyMap = new WeekdayTopicMap(new Dictionary<DayOfWeek, string>());
        Assert.Throws<InvalidOperationException>(() => emptyMap.CategoryFor(DayOfWeek.Monday));
    }
}
