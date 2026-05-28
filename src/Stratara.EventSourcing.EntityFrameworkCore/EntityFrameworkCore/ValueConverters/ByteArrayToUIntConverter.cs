using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stratara.EventSourcing.EntityFrameworkCore.ValueConverters;

/// <summary>
/// Value converter that stores a CLR <see cref="uint"/> as a 4-byte array column — used by
/// providers (like SQL Server) whose native row-version column is <c>byte[]</c> while the
/// domain models keep it as an unsigned integer.
/// </summary>
internal sealed class ByteArrayToUIntConverter() : ValueConverter<uint, byte[]>(v => BitConverter.GetBytes(v),
    v => BitConverter.ToUInt32(v, 0));
