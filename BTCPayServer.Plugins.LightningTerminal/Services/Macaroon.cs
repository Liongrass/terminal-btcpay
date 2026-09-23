using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.LightningTerminal.Services;

/// <summary>
/// Just enough of the macaroon v2 binary format to add a first-party caveat to one lnd baked.
/// </summary>
/// <remarks>
/// <para>
/// This is what <c>lncli restrictmacaroon</c> does, and litd does the same in
/// <c>macaroons.BakeSuperMacaroon</c>: lnd's <c>BakeMacaroon</c> RPC has no caveat field, so a caveat
/// can only be added by the holder afterwards. Adding one needs no secret - appending to the caveat
/// list and re-deriving the signature from the old one is the whole operation, which is precisely why
/// a macaroon can only ever be narrowed by whoever holds it, never widened.
/// </para>
/// <para>
/// Hand-rolled because the .NET macaroon packages on NuGet implement v1, and lnd emits v2 binary.
/// Checked against reference vectors produced by <c>gopkg.in/macaroon.v2</c>, the same library lnd
/// itself uses - see MacaroonTests.
/// </para>
/// </remarks>
internal sealed class Macaroon
{
    // Packet field types in the v2 binary encoding.
    private const byte FieldEos = 0;
    private const byte FieldLocation = 1;
    private const byte FieldIdentifier = 2;
    private const byte FieldVerificationId = 4;
    private const byte FieldSignature = 6;

    private const byte Version2 = 2;
    private const int SignatureLength = 32;

    private sealed record Caveat(byte[]? Location, byte[] Identifier, byte[]? VerificationId);

    private byte[]? _location;
    private byte[] _identifier = [];
    private readonly List<Caveat> _caveats = [];
    private byte[] _signature = [];

    public static Macaroon FromHex(string hex)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex.Trim());
        }
        catch (FormatException ex)
        {
            throw new FormatException($"Not a hex-encoded macaroon: {ex.Message}");
        }

        var reader = new Reader(bytes);
        var macaroon = new Macaroon();

        var version = reader.ReadByte();
        if (version != Version2)
            throw new FormatException($"Expected a version 2 macaroon, got version {version}.");

        // Header: an optional location then the identifier, terminated by an end-of-section packet.
        while (reader.PeekFieldType() is var field && field != FieldEos)
        {
            var (type, data) = reader.ReadPacket();
            switch (type)
            {
                case FieldLocation: macaroon._location = data; break;
                case FieldIdentifier: macaroon._identifier = data; break;
                default: throw new FormatException($"Unexpected field {type} in the macaroon header.");
            }
        }
        reader.ReadByte();

        // Caveats, each its own section, then an empty section closing the list.
        while (reader.PeekFieldType() != FieldEos)
        {
            byte[]? location = null, identifier = null, verificationId = null;
            while (reader.PeekFieldType() != FieldEos)
            {
                var (type, data) = reader.ReadPacket();
                switch (type)
                {
                    case FieldLocation: location = data; break;
                    case FieldIdentifier: identifier = data; break;
                    case FieldVerificationId: verificationId = data; break;
                    default: throw new FormatException($"Unexpected field {type} in a macaroon caveat.");
                }
            }
            reader.ReadByte();

            if (identifier is null)
                throw new FormatException("A macaroon caveat is missing its identifier.");
            macaroon._caveats.Add(new Caveat(location, identifier, verificationId));
        }
        reader.ReadByte();

        var (signatureField, signature) = reader.ReadPacket();
        if (signatureField != FieldSignature)
            throw new FormatException($"Expected the signature field, got field {signatureField}.");
        if (signature.Length != SignatureLength)
            throw new FormatException($"Expected a {SignatureLength}-byte signature, got {signature.Length}.");

        macaroon._signature = signature;
        return macaroon;
    }

    /// <summary>
    /// Narrows this macaroon by one first-party caveat, exactly as lnd's own client would.
    /// </summary>
    public void AddFirstPartyCaveat(string condition)
    {
        var identifier = Encoding.UTF8.GetBytes(condition);
        _caveats.Add(new Caveat(null, identifier, null));

        // The new signature is the old one keying an HMAC over the caveat. Chaining this way is what
        // makes the narrowing irreversible: recovering the previous signature would mean inverting it.
        _signature = HMACSHA256.HashData(key: _signature, source: identifier);
    }

    public string ToHex() => Convert.ToHexString(ToBytes()).ToLowerInvariant();

    private byte[] ToBytes()
    {
        var writer = new Writer();
        writer.WriteByte(Version2);

        if (_location is not null)
            writer.WritePacket(FieldLocation, _location);
        writer.WritePacket(FieldIdentifier, _identifier);
        writer.WriteByte(FieldEos);

        foreach (var caveat in _caveats)
        {
            if (caveat.Location is not null)
                writer.WritePacket(FieldLocation, caveat.Location);
            writer.WritePacket(FieldIdentifier, caveat.Identifier);
            if (caveat.VerificationId is not null)
                writer.WritePacket(FieldVerificationId, caveat.VerificationId);
            writer.WriteByte(FieldEos);
        }
        writer.WriteByte(FieldEos);

        writer.WritePacket(FieldSignature, _signature);
        return writer.ToArray();
    }

    /// <summary>Packets are a varint field type, a varint length, then that many bytes.</summary>
    private sealed class Reader(byte[] bytes)
    {
        private int _position;

        public byte ReadByte() =>
            _position < bytes.Length ? bytes[_position++] : throw new FormatException("Macaroon ended early.");

        public byte PeekFieldType() =>
            _position < bytes.Length ? bytes[_position] : throw new FormatException("Macaroon ended early.");

        public (byte Type, byte[] Data) ReadPacket()
        {
            var type = (byte)ReadVarint();
            var length = ReadVarint();
            if (length > int.MaxValue || _position + (int)length > bytes.Length)
                throw new FormatException("A macaroon packet runs past the end of the data.");

            var end = _position + (int)length;
            var data = bytes[_position..end];
            _position = end;
            return (type, data);
        }

        private ulong ReadVarint()
        {
            ulong value = 0;
            var shift = 0;
            while (true)
            {
                if (shift > 63)
                    throw new FormatException("A macaroon varint is too long.");
                var b = ReadByte();
                value |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                    return value;
                shift += 7;
            }
        }
    }

    private sealed class Writer
    {
        private readonly List<byte> _bytes = [];

        public void WriteByte(byte value) => _bytes.Add(value);

        public void WritePacket(byte type, byte[] data)
        {
            WriteVarint(type);
            WriteVarint((ulong)data.Length);
            _bytes.AddRange(data);
        }

        private void WriteVarint(ulong value)
        {
            while (value >= 0x80)
            {
                _bytes.Add((byte)(value | 0x80));
                value >>= 7;
            }
            _bytes.Add((byte)value);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
