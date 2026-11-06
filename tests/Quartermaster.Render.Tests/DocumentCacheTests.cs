using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class DocumentCacheTests
{
    [Fact]
    public void PathFor_ParentTraversal_StaysInsideDocsDir()
    {
        var cache = Path.Combine(Path.GetTempPath(), "qm-dc");
        var docs = Path.GetFullPath(Path.Combine(cache, "docs")) + Path.DirectorySeparatorChar;
        foreach (var n in new[] { "../x.pdf", "..\\..\\x.pdf", "a/../../x.pdf", "..", "" })
            Assert.StartsWith(docs, Path.GetFullPath(DocumentCache.PathFor(cache, n)));
    }

    [Fact]
    public void PathFor_SimilarNames_DoNotCollide()
    {
        Assert.NotEqual(DocumentCache.PathFor("c", "a/b.pdf"), DocumentCache.PathFor("c", "a_b.pdf"));
        Assert.Equal(DocumentCache.PathFor("c", "a/b.pdf"), DocumentCache.PathFor("c", "a/b.pdf"));
    }
}
