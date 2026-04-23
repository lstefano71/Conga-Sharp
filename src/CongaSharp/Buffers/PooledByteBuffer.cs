namespace CongaSharp.Buffers;

using System.Buffers;

/// <summary>
/// A pooled byte buffer implementing <see cref="IMemoryOwner{T}"/> for safe
/// ownership transfer through the pipeline. Rents from <see cref="ArrayPool{T}.Shared"/>
/// and returns on <see cref="Dispose"/>.
///
/// Ownership rules (per .NET Memory&lt;T&gt; guidelines):
/// - Rule #7: dispose it or transfer ownership, but not both.
/// - Rule #8: accepting an IMemoryOwner parameter means accepting ownership.
/// </summary>
internal sealed class PooledByteBuffer : IMemoryOwner<byte>
{
  private byte[]? _array;
  private readonly int _length;

  private PooledByteBuffer(byte[] array, int length)
  {
    _array = array;
    _length = length;
  }

  /// <summary>
  /// Rents a buffer of at least <paramref name="length"/> bytes from the shared pool.
  /// The returned <see cref="Memory"/> is sliced to exactly <paramref name="length"/>.
  /// </summary>
  public static PooledByteBuffer Rent(int length)
  {
    return new PooledByteBuffer(ArrayPool<byte>.Shared.Rent(length), length);
  }

  /// <summary>
  /// Wraps a buffer that was already rented from <see cref="ArrayPool{T}.Shared"/>.
  /// Takes ownership of <paramref name="rentedArray"/> — caller must not return it.
  /// </summary>
  public static PooledByteBuffer WrapRented(byte[] rentedArray, int length)
  {
    return new PooledByteBuffer(rentedArray, length);
  }

  /// <inheritdoc />
  /// <remarks>Sliced to the exact requested length (the backing array may be larger).</remarks>
  public Memory<byte> Memory => _array.AsMemory(0, _length);

  /// <summary>
  /// The underlying rented array. May be larger than <see cref="Length"/>.
  /// Use only when an API requires <c>byte[]</c> (e.g., <c>Stream.ReadAsync</c>).
  /// </summary>
  internal byte[] DangerousGetArray() => _array
      ?? throw new ObjectDisposedException(nameof(PooledByteBuffer));

  /// <summary>
  /// The actual data length (≤ backing array length).
  /// </summary>
  internal int Length => _length;

  /// <summary>
  /// Returns the buffer to <see cref="ArrayPool{T}.Shared"/>.
  /// Safe to call multiple times — only the first call returns the buffer.
  /// </summary>
  public void Dispose()
  {
    var arr = Interlocked.Exchange(ref _array, null);
    if (arr != null)
      ArrayPool<byte>.Shared.Return(arr);
  }
}
