using DesktopConcepts.Domain;

namespace DesktopConcepts.Tests.Domain;

public sealed class DailyConceptSetTests
{
    private static Concept MakeConcept(int n) =>
        new($"Title {n}", $"Explanation {n}", "Testing", DateOnly.FromDateTime(DateTime.Today));

    [Fact]
    public void GetByIndex_WrapsCorrectly()
    {
        var concepts = new[] { MakeConcept(0), MakeConcept(1), MakeConcept(2) };
        var set      = new DailyConceptSet(DateOnly.FromDateTime(DateTime.Today), concepts);

        Assert.Equal(concepts[0], set.GetByIndex(0));
        Assert.Equal(concepts[1], set.GetByIndex(1));
        Assert.Equal(concepts[2], set.GetByIndex(2));
        Assert.Equal(concepts[0], set.GetByIndex(3)); // wraps
        Assert.Equal(concepts[1], set.GetByIndex(4)); // wraps
    }

    [Fact]
    public void Count_IsThree()
    {
        var concepts = new[] { MakeConcept(0), MakeConcept(1), MakeConcept(2) };
        var set      = new DailyConceptSet(DateOnly.FromDateTime(DateTime.Today), concepts);
        Assert.Equal(3, set.Count);
    }
}
