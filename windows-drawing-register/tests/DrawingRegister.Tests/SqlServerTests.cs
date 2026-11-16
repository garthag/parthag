using DrawingRegister.Core;
using Xunit;

namespace DrawingRegister.Tests;

/// <summary>
/// Runs against a real SQL Server when DR_TEST_SQL holds a connection string
/// (CI sets it to LocalDB on Windows). Skipped otherwise.
/// </summary>
public class SqlServerTests
{
    static string? Conn => Environment.GetEnvironmentVariable("DR_TEST_SQL");

    [SkippableFactAttribute]
    public void RoundTripThroughSqlServer()
    {
        var store = new SqlDrawingStore(Conn!);
        store.EnsureDatabase();
        store.EnsureDatabase(); // re-runnable
        var batch = store.StartBatch("test.zip", "Plant T");
        var d = new Drawing
        {
            DrawingNo = "T-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", Plant = "Plant T", Unit = "Unit 1",
            TypeCode = "ST", Status = DrawingStatus.ForApproval, FileName = "t.pdf", BatchId = batch,
        };
        store.Add(d);
        Assert.True(d.Id > 0);
        d.Title = "Updated";
        d.Revision = "0";
        store.Update(d);
        store.FinishBatch(batch, 1, 0, 0);
        var back = store.All().Single(x => x.Id == d.Id);
        Assert.Equal("Updated", back.Title);
        Assert.Equal(DrawingStatus.ForApproval, back.Status);
        store.Delete(d.Id);
        Assert.DoesNotContain(store.All(), x => x.Id == d.Id);
    }

    sealed class SkippableFactAttribute : FactAttribute
    {
        public SkippableFactAttribute()
        {
            if (string.IsNullOrEmpty(Conn)) Skip = "Set DR_TEST_SQL to a SQL Server connection string to run.";
        }
    }
}
