namespace Plank.Writing;

public interface IRowWriterSlot
{
    void SerializeColumns();
    void WriteSerialized(RowGroupWriter rowGroupWriter);
    void ResetForReuse();
}
