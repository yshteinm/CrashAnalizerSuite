namespace CrashAnalyzer.Core.RAG;

public sealed class VectorStore
{
    private readonly List<VectorDocument> _documents = [];

    public int Count => _documents.Count;

    public void Add(string id, string content, IReadOnlyList<float> vector)
    {
        _documents.Add(new VectorDocument(id, content, vector.ToArray()));
    }

    public IReadOnlyList<VectorMatch> Search(IReadOnlyList<float> queryVector, int topK = 3)
    {
        return _documents
            .Select(d => new VectorMatch(d.Id, d.Content, CosineSimilarity(queryVector, d.Vector)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();
    }

    private static float CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || right.Count == 0 || left.Count != right.Count)
        {
            return 0f;
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;

        for (var i = 0; i < left.Count; i++)
        {
            dot += left[i] * right[i];
            leftMagnitude += left[i] * left[i];
            rightMagnitude += right[i] * right[i];
        }

        if (leftMagnitude == 0 || rightMagnitude == 0)
        {
            return 0f;
        }

        return (float)(dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude)));
    }

    private sealed record VectorDocument(string Id, string Content, float[] Vector);
}

public sealed record VectorMatch(string Id, string Content, float Score);
