using System.Collections.Concurrent;
using UnityEngine;

namespace Arena.Network
{
    public class ByteArrayPool
    {
        private readonly ConcurrentBag<byte[]> pool = new ();
        private readonly int bufferSize;
        private readonly int maxPoolSize;
        private int currentPoolSize = 0;

        public ByteArrayPool(int bufferSize, int maxPoolSize = 100)
        {
            this.bufferSize = bufferSize;
            this.maxPoolSize = maxPoolSize;
        }

        public byte[] Rent()
        {
            if (pool.TryTake(out var buffer))
            {
                System.Threading.Interlocked.Decrement(ref currentPoolSize);
                return buffer;
            }

            return new byte[bufferSize];
        }

        public void Return(byte[] buffer)
        {
            if (buffer == null || buffer.Length != bufferSize)
                return;

            if (currentPoolSize < maxPoolSize)
            {
                System.Array.Clear(buffer, 0, buffer.Length);
                pool.Add(buffer);
                System.Threading.Interlocked.Increment(ref currentPoolSize);
            }
        }

        public void Clear()
        {
            while (pool.TryTake(out _)) { }
            currentPoolSize = 0;
        }
    }
}