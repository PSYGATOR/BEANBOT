using System.Net;
using System.Text;

namespace BeanBot.Protocol.Hazel;

/// <summary>
/// Hazel MessageWriter adapted from Impostor.Hazel.MessageWriter.
/// </summary>
public sealed class MessageWriter : IMessageWriter, IRecyclable
{
    public const int DefaultBufferSize = 64000;
    private static readonly ObjectPoolCustom<MessageWriter> WriterPool =
        new(() => new MessageWriter(DefaultBufferSize));

    private readonly Stack<int> _messageStarts = new();

    public MessageWriter(byte[] buffer)
    {
        Buffer = buffer;
        Length = buffer.Length;
    }

    public MessageWriter(int bufferSize)
    {
        Buffer = new byte[bufferSize];
    }

    public MessageType SendOption { get; private set; }
    public byte[] Buffer { get; }
    public int Length { get; set; }
    public int Position { get; set; }

    public static MessageWriter Get(MessageType sendOption = MessageType.Unreliable)
    {
        var output = WriterPool.GetObject();
        output.Clear(sendOption);
        return output;
    }

    public byte[] ToByteArray(bool includeHeader)
    {
        if (includeHeader)
        {
            var output = new byte[Length];
            System.Buffer.BlockCopy(Buffer, 0, output, 0, Length);
            return output;
        }

        var headerSize = SendOption switch
        {
            MessageType.Reliable => 3,
            MessageType.Unreliable => 1,
            _ => throw new NotSupportedException($"Unsupported SendOption {SendOption}."),
        };

        var payloadLen = Math.Max(0, Length - headerSize);
        var payload = new byte[payloadLen];
        System.Buffer.BlockCopy(Buffer, headerSize, payload, 0, payloadLen);
        return payload;
    }

    public void StartMessage(byte typeFlag)
    {
        _messageStarts.Push(Position);
        Position += 2;
        Write(typeFlag);
    }

    public void EndMessage()
    {
        var lastMessageStart = _messageStarts.Pop();
        var length = (ushort)(Position - lastMessageStart - 3);
        Buffer[lastMessageStart] = (byte)length;
        Buffer[lastMessageStart + 1] = (byte)(length >> 8);
    }

    public void CancelMessage()
    {
        Position = _messageStarts.Pop();
        Length = Position;
    }

    public void Clear(MessageType sendOption)
    {
        _messageStarts.Clear();
        SendOption = sendOption;
        Buffer[0] = (byte)sendOption;
        switch (sendOption)
        {
            case MessageType.Reliable:
                Length = Position = 3;
                break;
            case MessageType.Unreliable:
            default:
                Length = Position = 1;
                break;
        }
    }

    public void Recycle()
    {
        Position = 0;
        Length = 0;
        _messageStarts.Clear();
        WriterPool.PutObject(this);
    }

    public void Write(bool value)
    {
        Buffer[Position++] = (byte)(value ? 1 : 0);
        UpdateLength();
    }

    public void Write(sbyte value)
    {
        Buffer[Position++] = (byte)value;
        UpdateLength();
    }

    public void Write(byte value)
    {
        Buffer[Position++] = value;
        UpdateLength();
    }

    public void Write(short value)
    {
        Buffer[Position++] = (byte)value;
        Buffer[Position++] = (byte)(value >> 8);
        UpdateLength();
    }

    public void Write(ushort value)
    {
        Buffer[Position++] = (byte)value;
        Buffer[Position++] = (byte)(value >> 8);
        UpdateLength();
    }

    public void Write(uint value)
    {
        Buffer[Position++] = (byte)value;
        Buffer[Position++] = (byte)(value >> 8);
        Buffer[Position++] = (byte)(value >> 16);
        Buffer[Position++] = (byte)(value >> 24);
        UpdateLength();
    }

    public void Write(int value)
    {
        Buffer[Position++] = (byte)value;
        Buffer[Position++] = (byte)(value >> 8);
        Buffer[Position++] = (byte)(value >> 16);
        Buffer[Position++] = (byte)(value >> 24);
        UpdateLength();
    }

    public void Write(ulong value)
    {
        Buffer[Position++] = (byte)value;
        Buffer[Position++] = (byte)(value >> 8);
        Buffer[Position++] = (byte)(value >> 16);
        Buffer[Position++] = (byte)(value >> 24);
        Buffer[Position++] = (byte)(value >> 32);
        Buffer[Position++] = (byte)(value >> 40);
        Buffer[Position++] = (byte)(value >> 48);
        Buffer[Position++] = (byte)(value >> 56);
        UpdateLength();
    }

    public void Write(long value)
    {
        Buffer[Position++] = (byte)value;
        Buffer[Position++] = (byte)(value >> 8);
        Buffer[Position++] = (byte)(value >> 16);
        Buffer[Position++] = (byte)(value >> 24);
        Buffer[Position++] = (byte)(value >> 32);
        Buffer[Position++] = (byte)(value >> 40);
        Buffer[Position++] = (byte)(value >> 48);
        Buffer[Position++] = (byte)(value >> 56);
        UpdateLength();
    }

    public unsafe void Write(float value)
    {
        fixed (byte* ptr = &Buffer[Position])
        {
            var valuePtr = (byte*)&value;
            *ptr = *valuePtr;
            *(ptr + 1) = *(valuePtr + 1);
            *(ptr + 2) = *(valuePtr + 2);
            *(ptr + 3) = *(valuePtr + 3);
        }

        Position += 4;
        UpdateLength();
    }

    public void Write(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WritePacked(bytes.Length);
        Write(bytes);
    }

    public void WriteBytesAndSize(byte[] bytes)
    {
        WritePacked((uint)bytes.Length);
        Write(bytes);
    }

    public void WriteBytesAndSize(byte[] bytes, int length)
    {
        WritePacked((uint)length);
        Write(bytes, length);
    }

    public void WriteBytesAndSize(byte[] bytes, int offset, int length)
    {
        WritePacked((uint)length);
        Write(bytes, offset, length);
    }

    public void Write(ReadOnlyMemory<byte> data) => Write(data.Span);

    public void Write(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Buffer.AsSpan(Position, bytes.Length));
        Position += bytes.Length;
        UpdateLength();
    }

    public void Write(byte[] bytes)
    {
        Array.Copy(bytes, 0, Buffer, Position, bytes.Length);
        Position += bytes.Length;
        UpdateLength();
    }

    public void Write(byte[] bytes, int offset, int length)
    {
        Array.Copy(bytes, offset, Buffer, Position, length);
        Position += length;
        UpdateLength();
    }

    public void Write(byte[] bytes, int length)
    {
        Array.Copy(bytes, 0, Buffer, Position, length);
        Position += length;
        UpdateLength();
    }

    public void WritePacked(int value) => WritePacked((uint)value);

    public void WritePacked(uint value)
    {
        do
        {
            var b = (byte)(value & 0xFF);
            if (value >= 0x80)
            {
                b |= 0x80;
            }

            Write(b);
            value >>= 7;
        } while (value > 0);
    }

    public void Write(IPAddress value) => Write(value.GetAddressBytes());

    public void Dispose() => Recycle();

    private void UpdateLength()
    {
        if (Position > Length)
        {
            Length = Position;
        }
    }
}
