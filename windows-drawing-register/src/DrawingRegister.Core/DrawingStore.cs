namespace DrawingRegister.Core;

/// <summary>Where register rows live. <see cref="SqlDrawingStore"/> in the app, in-memory in tests.</summary>
public interface IDrawingStore
{
    List<Drawing> All();
    void Add(Drawing d);
    void Update(Drawing d);
    void Delete(int id);
    int StartBatch(string source, string defaultPlant);
    void FinishBatch(int batchId, int added, int duplicates, int errors);
}

public sealed class InMemoryDrawingStore : IDrawingStore
{
    readonly List<Drawing> _rows = [];
    int _next = 1, _batch;

    public List<Drawing> All() => _rows.Select(d => d.Clone()).ToList();
    public void Add(Drawing d) { d.Id = _next++; _rows.Add(d.Clone()); }
    public void Update(Drawing d) { var i = _rows.FindIndex(x => x.Id == d.Id); if (i >= 0) _rows[i] = d.Clone(); }
    public void Delete(int id) => _rows.RemoveAll(x => x.Id == id);
    public int StartBatch(string source, string defaultPlant) => ++_batch;
    public void FinishBatch(int batchId, int added, int duplicates, int errors) { }
}
