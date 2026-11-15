using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.ObjectPool;

namespace BeanBot.Protocol.Hazel;

/// <summary>
/// Bounds-checked Hazel MessageReader adapted from Impostor.Hazel.MessageReader.
/// </summary>
public sealed class MessageReader : IMessageReader
{
    private const int MaxPackedInt32Bytes = 5;
    private static readonly ObjectPool<MessageReader> DefaultPool =
        new DefaultObjectPool<MessageReader>(new DefaultMessageReaderPolicy(), 256);

    private readonly ObjectPool<MessageReader> _pool;
    private bool _inUse;

    public MessageReader(ObjectPool<MessageReader>? pool = null)
    {
        _pool = pool ?? DefaultPool;
    }

    public byte[] Buffer { get; private set; } = Array.Empty<byte>();
    public int Offset { get; internal set; }
    public int Position { get; internal set; }
    public int Length { get; internal set; }
    public int BytesRemaining => Length - Position;
    public byte Tag { get; private set; } = byte.MaxValue;
    public MessageReader? Parent { get; private set; }
    private int ReadPosition => Offset + Position;

    public static MessageReader Get(byte[] buffer, int offset = 0, int? length = null, byte tag = byte.MaxValue)
    {
        var reader = DefaultPool.Get();
        reader.Update(buffer, offset, 0, length, tag);
        return reader;
    }

    public void Update(byte[] buffer, int offset = 0, int position = 0, int? length = null, byte tag = byte.MaxValue, MessageReader? parent = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var effectiveLength = length ?? (buffer.Length - offset);
        if (offset < 0 || effectiveLength < 0 || offset + effectiveLength > buffer.Length)
        {
            throw new ProtocolException(tag == byte.MaxValue ? null : tag, buffer,
                $"Invalid reader bounds: offset={offset}, length={effectiveLength}, bufferLength={buffer.Length}.");
        }

        _inUse = true;
        Buffer = buffer;
        Offset = offset;
        Position = position;
        Length = effectiveLength;
        Tag = tag;
        Parent = parent;
    }

    internal void Reset()
    {
        _inUse = false;
        Tag = byte.MaxValue;
        Buffer = Array.Empty<byte>();
        Offset = 0;
        Position = 0;
        Length = 0;
        Parent = null;
    }

    public IMessageReader ReadMessage()
    {
        EnsureAvailable(sizeof(ushort) + sizeof(byte));
        var length = ReadUInt16();
        var tag = FastByte();
        EnsureAvailable(length, tag);

        var pos = ReadPosition;
        Position += length;

        var reader = _pool.Get();
        reader.Update(Buffer, pos, 0, length, tag, this);
        return reader;
    }

    public void RemoveMessage(IMessageReader message)
    {
        if (message.Buffer != Buffer)
        {
            throw new InvalidOperationException("Tried to remove message from a different buffer.");
        }

        var offsetStart = message.Offset - 3;
        var offsetEnd = message.Offset + message.Length;
        var lengthToCopy = Buffer.Length - offsetEnd;

        System.Buffer.BlockCopy(Buffer, offsetEnd, Buffer, offsetStart, lengthToCopy);
        ((MessageReader)message).Parent?.AdjustLength(message.Offset, message.Length + 3);
    }

    private void AdjustLength(int offset, int amount)
    {
        Length -= amount;
        if (ReadPosition > offset)
        {
            Position -= amount;
        }

        if (Parent != null)
        {
            var lengthOffset = Offset - 3;
            var curLen = Buffer[lengthOffset] | (Buffer[lengthOffset + 1] << 8);
            curLen -= amount;
            Buffer[lengthOffset] = (byte)curLen;
            Buffer[lengthOffset + 1] = (byte)(curLen >> 8);
            Parent.AdjustLength(offset, amount);
        }
    }

    public void Dispose()
    {
        if (_inUse)
        {
            _pool.Return(this);
        }
    }

    public bool ReadBoolean() => FastByte() != 0;
    public sbyte ReadSByte() => (sbyte)FastByte();
    public byte ReadByte() => FastByte();

    public ushort ReadUInt16()
    {
        EnsureAvailable(sizeof(ushort));
        var output = BinaryPrimitives.ReadUInt16LittleEndian(Buffer.AsSpan(ReadPosition));
        Position += sizeof(ushort);
        return output;
    }

    public short ReadInt16()
    {
        EnsureAvailable(sizeof(short));
        var output = BinaryPrimitives.ReadInt16LittleEndian(Buffer.AsSpan(ReadPosition));
        Position += sizeof(short);
        return output;
    }

    public uint ReadUInt32()
    {
        EnsureAvailable(sizeof(uint));
        var output = BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(ReadPosition));
        Position += sizeof(uint);
        return output;
    }

    public int ReadInt32()
    {
        EnsureAvailable(sizeof(int));
        var output = BinaryPrimitives.ReadInt32LittleEndian(Buffer.AsSpan(ReadPosition));
        Position += sizeof(int);
        return output;
    }

    public ulong ReadUInt64()
    {
        EnsureAvailable(sizeof(ulong));
        var output = BinaryPrimitives.ReadUInt64LittleEndian(Buffer.AsSpan(ReadPosition));
        Position += sizeof(ulong);
        return output;
    }

    public long ReadInt64()
    {
        EnsureAvailable(sizeof(long));
        var output = BinaryPrimitives.ReadInt64LittleEndian(Buffer.AsSpan(ReadPosition));
        Position += sizeof(long);
        return output;
    }

    public float ReadSingle()
    {
        EnsureAvailable(sizeof(float));
        var output = BinaryPrimitives.ReadSingleLittleEndian(Buffer.AsSpan(ReadPosition));
        Position += sizeof(float);
        return output;
    }

    public string ReadString(int length)
    {
        if (length < 0)
        {
            throw new ProtocolException(CurrentPacketId(), CurrentSlice(), $"Negative string length {length}.");
        }

        EnsureAvailable(length);
        var output = Encoding.UTF8.GetString(Buffer.AsSpan(ReadPosition, length));
        Position += length;
        return output;
    }

    public string ReadString()
    {
        var len = ReadPackedInt32();
        return ReadString(len);
    }

    public ReadOnlyMemory<byte> ReadBytesAndSize()
    {
        var len = ReadPackedInt32();
        return ReadBytes(len);
    }

    public ReadOnlyMemory<byte> ReadBytes(int length)
    {
        if (length < 0)
        {
            throw new ProtocolException(CurrentPacketId(), CurrentSlice(), $"Negative byte array length {length}.");
        }

        EnsureAvailable(length);
        var output = Buffer.AsMemory(ReadPosition, length);
        Position += length;
        return output;
    }

    public int ReadPackedInt32() => (int)ReadPackedUInt32();

    public uint ReadPackedUInt32()
    {
        var shift = 0;
        uint output = 0;

        for (var i = 0; i < MaxPackedInt32Bytes; i++)
        {
            var b = FastByte();
            var hasMore = b >= 0x80;
            if (hasMore)
            {
                b ^= 0x80;
            }

            output |= (uint)b << shift;
            if (!hasMore)
            {
                return output;
            }

            shift += 7;
        }

        throw new ProtocolException(CurrentPacketId(), CurrentSlice(), "Malformed packed UInt32 exceeds 5 bytes.");
    }

    public void CopyTo(IMessageWriter writer)
    {
        writer.Write((ushort)Length);
        writer.Write(Tag);
        writer.Write(Buffer.AsMemory(Offset, Length));
    }

    public IMessageReader Copy(int offset = 0)
    {
        if (offset < 0 || offset > Length)
        {
            throw new ProtocolException(CurrentPacketId(), CurrentSlice(), $"Invalid Copy offset {offset} for length {Length}.");
        }

        var reader = _pool.Get();
        reader.Update(Buffer, Offset + offset, Math.Max(0, Position - offset), Length - offset, Tag, Parent);
        return reader;
    }

    public void Seek(int position)
    {
        if (position < 0 || position > Length)
        {
            throw new ProtocolException(CurrentPacketId(), CurrentSlice(), $"Invalid Seek position {position} for length {Length}.");
        }

        Position = position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte FastByte()
    {
        EnsureAvailable(1);
        return Buffer[Offset + Position++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureAvailable(int needed, byte? overrideTag = null)
    {
        if (needed < 0 || Position + needed > Length)
        {
            throw new ProtocolException(
                overrideTag ?? CurrentPacketId(),
                CurrentSlice(),
                $"Truncated Hazel payload: needed {needed} bytes at pos {Position}, only {BytesRemaining} remaining (len={Length}).");
        }
    }

    private byte? CurrentPacketId() => Tag == byte.MaxValue ? null : Tag;

    private ReadOnlySpan<byte> CurrentSlice()
    {
        if (Buffer.Length == 0 || Offset < 0 || Offset >= Buffer.Length)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        var len = Math.Clamp(Length, 0, Buffer.Length - Offset);
        return Buffer.AsSpan(Offset, len);
    }

    private sealed class DefaultMessageReaderPolicy : PooledObjectPolicy<MessageReader>
    {
        public override MessageReader Create() => new(DefaultPool);

        public override bool Return(MessageReader obj)
        {
            obj.Reset();
            return true;
        }
    }
}
