using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PeopleWorks.DBFSync;

public sealed class RowHasher : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public byte[] Compute(IReadOnlyList<DbfColumn> columns, IReadOnlyList<object?> values)
    {
        if (columns.Count != values.Count)
            throw new ArgumentException(L10n.T("ColumnsValuesMismatch"));

        for (int i = 0; i < columns.Count; i++)
        {
            object? value = values[i];
            AppendByte(value is null ? (byte)0 : (byte)1);
            AppendByte((byte)columns[i].Kind);
            if (value is null)
                continue;

            if (value is byte[] bytes)
            {
                AppendLength(bytes.Length);
                _hash.AppendData(bytes);
                continue;
            }

            string canonical = value switch
            {
                DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                decimal number => number.ToString(CultureInfo.InvariantCulture),
                double number => number.ToString("R", CultureInfo.InvariantCulture),
                float number => number.ToString("R", CultureInfo.InvariantCulture),
                bool logical => logical ? "1" : "0",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };
            byte[] encoded = Encoding.UTF8.GetBytes(canonical);
            AppendLength(encoded.Length);
            _hash.AppendData(encoded);
        }

        return _hash.GetHashAndReset();
    }

    public void Dispose() => _hash.Dispose();

    private void AppendByte(byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        _hash.AppendData(buffer);
    }

    private void AppendLength(int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        _hash.AppendData(buffer);
    }
}
