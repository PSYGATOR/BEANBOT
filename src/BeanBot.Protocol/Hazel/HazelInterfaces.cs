namespace BeanBot.Protocol.Hazel;

public interface IRecyclable
{
    void Recycle();
}

public interface IMessageReader : IDisposable
{
    byte Tag { get; }
    byte[] Buffer { get; }
    int Offset { get; }
    int Position { get; }
    int Length { get; }
    int BytesRemaining { get; }

    IMessageReader ReadMessage();
    bool ReadBoolean();
    sbyte ReadSByte();
    byte ReadByte();
    ushort ReadUInt16();
    short ReadInt16();
    uint ReadUInt32();
    int ReadInt32();
    ulong ReadUInt64();
    long ReadInt64();
    float ReadSingle();
    string ReadString(int length);
    string ReadString();
    ReadOnlyMemory<byte> ReadBytesAndSize();
    ReadOnlyMemory<byte> ReadBytes(int length);
    int ReadPackedInt32();
    uint ReadPackedUInt32();
    void CopyTo(IMessageWriter writer);
    void Seek(int position);
    void RemoveMessage(IMessageReader message);
    IMessageReader Copy(int offset = 0);
}

public interface IMessageWriter : IDisposable
{
    byte[] Buffer { get; }
    int Length { get; set; }
    int Position { get; set; }
    MessageType SendOption { get; }

    byte[] ToByteArray(bool includeHeader);
    void Write(bool value);
    void Write(sbyte value);
    void Write(byte value);
    void Write(short value);
    void Write(ushort value);
    void Write(uint value);
    void Write(int value);
    void Write(ulong value);
    void Write(long value);
    void Write(float value);
    void Write(string value);
    void Write(System.Net.IPAddress value);
    void WritePacked(int value);
    void WritePacked(uint value);
    void Write(ReadOnlyMemory<byte> data);
    void Write(ReadOnlySpan<byte> data);
    void Write(byte[] bytes);
    void WriteBytesAndSize(byte[] bytes);
    void WriteBytesAndSize(byte[] bytes, int length);
    void WriteBytesAndSize(byte[] bytes, int offset, int length);
    void StartMessage(byte typeFlag);
    void EndMessage();
    void CancelMessage();
    void Clear(MessageType type);
}
