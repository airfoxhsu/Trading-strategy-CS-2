using System;
using System.Collections;
using System.Collections.Generic;

namespace ExtremeSignalAppCS.Models
{
    /// <summary>
    /// 高效能不可變分段列表。
    /// 專為高頻 Tick 行情設計的資料結構，能以 O(1) 時間成本建立資料快照，
    /// 徹底消滅 lock 區塊內數十萬筆 List<T> 深拷貝帶來的效能瓶頸。
    /// </summary>
    /// <typeparam name="T">Tick 資料型別</typeparam>
    public class ImmutableSegmentList<T> : IReadOnlyList<T>
    {
        private const int SegmentSize = 16384; // 每個區塊 16K 筆 (適合 CPU Cache)
        
        // 凍結的完整區塊
        private readonly List<T[]> _frozenSegments;
        
        // 目前正在寫入的活動區塊
        private T[] _activeSegment;
        
        // 活動區塊內的寫入位置 (同時也是活動區塊內的資料筆數)
        private int _activeCount;
        
        // 總筆數
        private int _totalCount;

        /// <summary>
        /// 預設建構子
        /// </summary>
        public ImmutableSegmentList()
        {
            _frozenSegments = new List<T[]>();
            _activeSegment = new T[SegmentSize];
            _activeCount = 0;
            _totalCount = 0;
        }

        /// <summary>
        /// 提供給 Snapshot 建立使用的私有建構子
        /// </summary>
        private ImmutableSegmentList(List<T[]> frozenSegments, T[] frozenActiveSegment, int activeCount, int totalCount)
        {
            // O(1) 共享指標
            _frozenSegments = frozenSegments;
            
            // 由於這是 Snapshot，當時的 _activeSegment 也被我們當作 frozen 對待
            // 所以 Snapshot 不會有新的寫入，這裡只需把 Snapshot 的 active 區塊也凍結
            var snapshotFrozenSegments = new List<T[]>(frozenSegments);
            
            if (activeCount > 0)
            {
                // 我們只把 active 區塊有資料的部分剪裁出來變成最後一個 frozen 區塊
                T[] tail = new T[activeCount];
                Array.Copy(frozenActiveSegment, tail, activeCount);
                snapshotFrozenSegments.Add(tail);
            }

            _frozenSegments = snapshotFrozenSegments;
            _activeSegment = Array.Empty<T>();
            _activeCount = 0;
            _totalCount = totalCount;
        }

        /// <summary>
        /// 新增一筆資料 (O(1) 攤提時間)
        /// 此方法只能由寫入端 (COM 事件) 呼叫。
        /// </summary>
        public void Add(T item)
        {
            if (_activeCount == SegmentSize)
            {
                // 區塊已滿，將其凍結，並配置新的活動區塊
                _frozenSegments.Add(_activeSegment);
                _activeSegment = new T[SegmentSize];
                _activeCount = 0;
            }

            _activeSegment[_activeCount++] = item;
            _totalCount++;
        }

        /// <summary>
        /// 批次新增多筆資料 (O(K) 時間)
        /// 此方法只能由寫入端 (COM 事件) 呼叫。
        /// </summary>
        public void AddRange(IEnumerable<T> items)
        {
            foreach (var item in items)
            {
                Add(item);
            }
        }

        /// <summary>
        /// 建立當前資料的唯讀快照 (O(1) 複雜度，不需拷貝數十萬筆資料)
        /// 返回的物件可以在其他執行緒中安全迭代，不會被後續的 Add 影響。
        /// </summary>
        public ImmutableSegmentList<T> CreateSnapshot()
        {
            // 將當前的狀態封裝成 Snapshot
            // 注意：List<T[]> 拷貝是 O(N)，但 N 是區塊數量。
            // 例如 30 萬筆 Tick，區塊數僅 300000 / 16384 = 18 個指標拷貝，成本幾乎為零。
            return new ImmutableSegmentList<T>(new List<T[]>(_frozenSegments), _activeSegment, _activeCount, _totalCount);
        }

        /// <summary>
        /// 清空資料
        /// </summary>
        public void Clear()
        {
            _frozenSegments.Clear();
            _activeSegment = new T[SegmentSize];
            _activeCount = 0;
            _totalCount = 0;
        }

        // --- IReadOnlyList<T> 實作 ---

        public int Count => _totalCount;

        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= _totalCount)
                    throw new ArgumentOutOfRangeException(nameof(index));

                int segmentIndex = index / SegmentSize;
                int offset = index % SegmentSize;

                if (segmentIndex < _frozenSegments.Count)
                {
                    return _frozenSegments[segmentIndex][offset];
                }
                
                // 最後的零碎區塊
                return _activeSegment[offset];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            // 迭代所有凍結區塊
            foreach (var segment in _frozenSegments)
            {
                for (int i = 0; i < segment.Length; i++)
                {
                    yield return segment[i];
                }
            }

            // 迭代活動區塊的有效部分
            for (int i = 0; i < _activeCount; i++)
            {
                yield return _activeSegment[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
