using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Robust.Shared.Utility
{

    public class LazyList<T> : IReadOnlyList<T>, IDisposable
    {
        private List<T> _back;
        private IEnumerable<T> _src;
        private IEnumerator<T> _cursor;
        private bool _dead = false;
        private int _max;
        private int? _length;

        T IReadOnlyList<T>.this[int index] {
            get {
                AdvanceUntil(index);
                return _back[index];
            }
        }

        int IReadOnlyCollection<T>.Count {
           get {
                // Happy path
                if (_length is not null)
                    return (int) _length!;
                
                // Unhappy path 
                ReadAll();
                return _max;
           }
        }

        private void ReadAll() // 😢️
        {
            while(_cursor.MoveNext())
            {
                _back.Add(_cursor.Current);
                _max++;
            }
        }

        private void AdvanceUntil(int n)
        {
            if (_max > n)
                return;

            while(_max < n)
            {
                if (!_cursor.MoveNext())
                    throw new ArgumentOutOfRangeException();

                _back.Add(_cursor.Current);
                _max++;
            }
            return;
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            foreach(var i in _back)
                yield return i;

            if (_dead)
                yield break;

            while(true) {
                if (!_cursor.MoveNext()) {
                    _dead = true;
                    yield break;
                }

                var i = _cursor.Current;
                _back.Add(i);
                _max++;
                yield return i;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            foreach(var i in _back)
                yield return i;

            if (_dead)
                yield break;

            while(true) {
                if (!_cursor.MoveNext()) {
                    _dead = true;
                    yield break;
                }

                var i = _cursor.Current;
                _back.Add(i);
                _max++;
                yield return i;
            }
        }

        public void Dispose()
        {
            _cursor.Dispose();
        }

        public LazyList(IEnumerable<T> src, int? length = default!)
        {
            _length = length;
            _src = src;
            _cursor = _src.GetEnumerator();
            if (length is not null) {
                _back = new List<T>((int) length!);
            } else {
                _back = new List<T>();
            }
        }
    }

    // ListOfLists acts like one list, while the backing storage is actually
    // a bunch of lists.
    //
    // The index of item 2 in list 5 is (lists[0].Count() + lists[1].Count() ... lists[4].Count() + 2)
    public class ListOfLists<T> : IList<T>, ICollection<T>
    {
        private IReadOnlyList<IList<T>> _lists;

        public ListOfLists(params IList<T>[] args) => _lists = args;
        public ListOfLists(LazyList<IList<T>> args) => _lists = args;
        public ListOfLists(IEnumerable<IList<T>> args) : this(new LazyList<IList<T>>(args)) {}

        public bool TryGetOwner(int index, [NotNullWhen(true)] out IList<T>? owner, [NotNullWhen(true)] out int? pos) {
            var ctr = index;
            var li = 0;

            // Skip to the list that the index belongs to
            while (li < _lists.Count) {
                var c = _lists[li].Count;
                if (ctr - c < 0)
                    break;
                ctr -= _lists[li].Count;
                li++;
            }

            // Die
            if (li == _lists.Count) {
                owner = default!;
                pos = default!;
                return false;
            }

            // or not
            owner = _lists[li];
            pos = ctr;
            return true;
        }

        public IList<T> GetOwner(int index, out int pos)
        {
            if (TryGetOwner(index, out var ret, out var npos))
            {
                pos = (int) npos!;
                return ret;
            }

            throw new ArgumentOutOfRangeException();
        }

        public IList<T> GetOwner(int index) => GetOwner(index, out var _);


        // Interfaces N' Stuff

        /// <inheritdoc />
        public int Count => _lists.Select((l) => l.Count).Aggregate(0, (o, l) => o+l);

        /// <inheritdoc />
        public bool IsReadOnly => false;

        /// <inheritdoc />
        public T this[int index] {
            get => GetOwner(index, out var pos)[pos];
            set => GetOwner(index, out var pos)[pos] = value;
        }

        /// <inheritdoc />
        public int IndexOf(T item)
        {
            var ctr = 0;
            foreach (var list in _lists) {
                var idx = list.IndexOf(item);
                if (idx != -1)
                    return idx + ctr;

                ctr += list.Count;
            }
            return -1;
        }

        /// <inheritdoc />
        public void Insert(int index, T item) => GetOwner(index, out var pos).Insert(pos, item);

        /// <inheritdoc />
        public void RemoveAt(int index) => GetOwner(index, out var pos).RemoveAt(pos);

        /// <inheritdoc />
        public void Add(T item) => _lists[_lists.Count-1].Add(item);

        /// <inheritdoc />
        public void Clear()
        {
            foreach (var list in _lists) {
                list.Clear();
            }
            _lists = new IList<T>[]{ new List<T>() };
        }

        /// <inheritdoc />
        public bool Contains(T item)
        {
            foreach (var list in _lists) {
                if (list.Contains(item))
                    return true;
            }
            return false;
        }

        /// <inheritdoc />
        public void CopyTo(T[] array, int arrayIndex)
        {
            if (array == null)
                throw new ArgumentNullException();

            if (arrayIndex < 0)
                throw new ArgumentOutOfRangeException();

            if (Count > ((array.Length)-arrayIndex))
                throw new ArgumentException();

            var ctr = arrayIndex;
            foreach (var list in _lists) {
                foreach (var i in list) {
                    array[ctr++] = i;
                }
            }
        }

        /// <inheritdoc />
        public bool Remove(T item)
        {
            foreach (var list in _lists) {
                if (list.Remove(item))
                    return true;
            }
            return false;
        }

        /// <inheritdoc />
        public IEnumerator<T> GetEnumerator() => new Enumerator(this);

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

        private class Enumerator : IEnumerator<T>
        {
            private ListOfLists<T> _parent;
            private int _idx;

            public Enumerator(ListOfLists<T> parent)
            {
                _parent = parent;
            }

            /// <inheritdoc />
            public T Current => _parent[_idx];

            /// <inheritdoc />
            object IEnumerator.Current => _parent[_idx]!;

            // What's it supposed to do, delete the int?
            void IDisposable.Dispose() {}

            /// <inheritdoc />
            bool IEnumerator.MoveNext()
            {
                _idx++;
                if (_idx >= _parent.Count)
                    return false;

                return true;
            }

            /// <inheritdoc />
            void IEnumerator.Reset() => _idx = 0;
        }
    }
}
