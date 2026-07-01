using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// 高效能、無鎖讀寫 (Lock-Free) 的僅附加 (Append-Only) 容器。
    /// 參考 QuantConnect/Lean 架構，避免了任何 OS 鎖以及 GC Allocation。
    /// 適用於高頻 Tick 資料流（如每秒 10,000+ Ticks）的極限場景。
    /// </summary>
    /// <remarks>
    /// <para><b>執行緒安全契約：</b></para>
    /// <list type="bullet">
    ///   <item><description><c>Add()</c>：多執行緒並行呼叫安全 (Lock-Free)。</description></item>
    ///   <item><description><c>this[index]</c>、<c>Count</c>：多執行緒讀取安全 (Lock-Free)。</description></item>
    ///   <item><description><c>Clear()</c>：可與並行的 <c>Add()</c> 安全共存。內部透過世代計數器 (Generation Counter) 通知正在 SpinWait 的 <c>Add()</c> 提前放棄，避免死鎖。</description></item>
    /// </list>
    /// </remarks>
    /// <typeparam name="T">元素型別，建議為 struct 以達成零 GC 分配</typeparam>
    public class ConcurrentAppendOnlyList<T> : IReadOnlyList<T>
    {
        // 每個 Block 可容納的元素數量 (16384 是適合高頻快取的 2 的次方數)
        private const int BlockSize = 16384;
        
        // 預分配 Block 指標陣列，8192 個 Block 可容納高達 1.34 億筆資料。
        // 此設計確保在新增元素時，不會發生如同 List<T> 的動態擴容陣列搬移。
        private readonly T[][] _blocks = new T[8192][];
        
        private int _count = 0;
        
        // 分離寫入指標與確認指標，確保高頻狀態下讀取不會遇到尚未寫入的 default 值
        private int _writeIndex = 0;
        
        // 世代計數器：每次 Clear() 時遞增，使正在 SpinWait 的 Add() 偵測到清空事件並安全放棄
        private int _generation = 0;

        /// <summary>
        /// 獲取目前集合中的元素總數。
        /// 使用 Volatile.Read 確保多執行緒下能無鎖讀取到最新寫入的數量。
        /// </summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>
        /// 依索引獲取元素。讀取完全無鎖，效能趨近於原生二維陣列。
        /// </summary>
        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= Count)
                    throw new ArgumentOutOfRangeException(nameof(index), $"Index: {index}, Count: {Count}");
                
                int blockIndex = index / BlockSize;
                int localIndex = index % BlockSize;
                return _blocks[blockIndex][localIndex];
            }
        }

        /// <summary>
        /// 新增元素至集合末端。
        /// 完全無鎖 (Lock-Free)，透過 Interlocked 進行原子增量。
        /// 若在 SpinWait 期間偵測到 <see cref="Clear"/> 被呼叫（世代變更），則安全放棄本次寫入。
        /// </summary>
        public void Add(T item)
        {
            // 0. 快照當前世代，用於偵測 Clear() 是否在 Add() 途中被呼叫
            int gen = Volatile.Read(ref _generation);

            // 1. 原子取得當前要寫入的索引
            int index = Interlocked.Increment(ref _writeIndex) - 1;
            int blockIndex = index / BlockSize;
            int localIndex = index % BlockSize;

            if (blockIndex >= _blocks.Length)
                throw new InvalidOperationException("超出最大容量限制 (1.34億筆)。");

            // 2. 確保 Block 已經實例化 (Lazy Allocation, 無鎖 CompareExchange)
            if (Volatile.Read(ref _blocks[blockIndex]) == null)
            {
                var newBlock = new T[BlockSize];
                Interlocked.CompareExchange(ref _blocks[blockIndex], newBlock, null);
            }

            // 3. 寫入資料
            _blocks[blockIndex][localIndex] = item;

            // 4. SpinWait 確保 Count 順序遞增 (保證 Count 反映的是連續且已寫入的資料區段)
            var spinWait = new SpinWait();
            while (Volatile.Read(ref _count) != index)
            {
                // 世代已變更 = 被 Clear() 了，放棄本次寫入以避免永久自旋死鎖
                if (Volatile.Read(ref _generation) != gen) return;
                spinWait.SpinOnce();
            }
            
            // 最終世代確認：防止在 _count 恰好等於 index 的瞬間被 Clear() 歸零後錯誤推進
            if (Volatile.Read(ref _generation) != gen) return;

            // 將 _count 推進，標記為安全可讀取 (Memory Barrier 效果)
            Volatile.Write(ref _count, index + 1);
        }

        /// <summary>
        /// 批次寫入多個元素。
        /// </summary>
        public void AddRange(IEnumerable<T> items)
        {
            foreach (var item in items)
            {
                Add(item);
            }
        }

        /// <summary>
        /// 高效清空集合（直接將計數器歸零，重用已配置的底層陣列）。
        /// 徹底避免重新配置陣列的記憶體與 GC 消耗 (Zero Allocation)。
        /// 先遞增世代計數器，通知所有正在 SpinWait 的 <see cref="Add"/> 安全放棄，再歸零指標。
        /// </summary>
        public void Clear()
        {
            // 1. 遞增世代：通知所有正在 SpinWait 的 Add() 偵測到清空並安全放棄
            Interlocked.Increment(ref _generation);
            
            // 2. 歸零寫入指標與計數器。舊資料會在新資料寫入時被覆蓋。
            Volatile.Write(ref _writeIndex, 0);
            Volatile.Write(ref _count, 0);
        }

        /// <summary>
        /// 取得零分配 (Zero Allocation) 走訪器。
        /// 在 foreach 使用時，由於回傳的是 struct Enumerator，不會產生任何 GC 配置。
        /// </summary>
        public Enumerator GetEnumerator() => new Enumerator(this);

        // 隱含實作介面 (會發生 boxing，但 foreach 編譯器優化下會自動呼叫實體方法)
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Struct 型別走訪器，消除 foreach 迴圈帶來的 Heap 記憶體分配。
        /// </summary>
        public struct Enumerator : IEnumerator<T>
        {
            private readonly ConcurrentAppendOnlyList<T> _list;
            private readonly int _countSnapshot;
            private int _currentIndex;

            internal Enumerator(ConcurrentAppendOnlyList<T> list)
            {
                _list = list;
                _countSnapshot = list.Count; // 鎖定當下總數，防止走訪期間越界
                _currentIndex = -1;
            }

            public T Current
            {
                get
                {
                    if (_currentIndex < 0 || _currentIndex >= _countSnapshot)
                        throw new InvalidOperationException();
                    
                    int blockIndex = _currentIndex / BlockSize;
                    int localIndex = _currentIndex % BlockSize;
                    return _list._blocks[blockIndex][localIndex];
                }
            }

            object? IEnumerator.Current => Current;

            public bool MoveNext()
            {
                _currentIndex++;
                return _currentIndex < _countSnapshot;
            }

            public void Reset()
            {
                _currentIndex = -1;
            }

            public void Dispose()
            {
                // struct 不需要實作 Dispose
            }
        }
    }
}
