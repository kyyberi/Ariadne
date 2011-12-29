﻿// © 2011 Jon Hanna.
// This source code is licensed under the EUPL, Version 1.1 only (the “Licence”).
// You may not use, modify or distribute this work except in compliance with the Licence.
// You may obtain a copy of the Licence at:
// <http://joinup.ec.europa.eu/software/page/eupl/licence-eupl>
// A copy is also distributed with this source code.
// Unless required by applicable law or agreed to in writing, software distributed under the
// Licence is distributed on an “AS IS” basis, without warranties or conditions of any kind.

// The algorithm here is a simplification of that used for the LockFreeDictionary class,
// but excluding the work necessary to handle the value part of key-value pairs.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Permissions;
using System.Threading;

namespace HackCraft.LockFree
{
    /// <summary>A hash-based set which is thread-safe for all operations, without locking.
    /// </summary>
    [Serializable]
    public sealed class LockFreeSet<T> : ISet<T>, ICloneable, IProducerConsumerCollection<T>, ISerializable
    {
        private const int REPROBE_LOWER_BOUND = 5;
        private const int REPROBE_SHIFT = 5;
        private const int ZERO_HASH = 0x55555555;
        internal class Box
        {
            public T Value;
            public Box(T value)
            {
                Value = value;
            }
            public void FillPrime(PrimeBox box)
            {
                box.Value = Value;
            }
            public Box StripPrime()
            {
                return this is PrimeBox ? new Box(Value) : this;
            }
            public static readonly TombstoneBox DeadItem = new TombstoneBox();
        }
        internal sealed class PrimeBox : Box
        {
            public PrimeBox(T value)
                :base(value){}
        }
        internal sealed class TombstoneBox : Box
        {
            public TombstoneBox()
                :base(default(T)){}
            public TombstoneBox(T value)
                :base(value){}
        }
        [StructLayoutAttribute(LayoutKind.Sequential, Pack=1)]
        private struct Record
        {
            public int Hash;
            public Box Box;
        }
        private sealed class Table
        {
            public readonly Record[] Records;
            public volatile Table Next;
            public readonly AliasedInt Size;
            public readonly AliasedInt Slots = new AliasedInt();
            public readonly int Capacity;
            public readonly int Mask;
            public readonly int PrevSize;
            public readonly int ReprobeLimit;
            public int CopyIdx;
            public int Resizers;
            public int CopyDone;
            public Table(int capacity, AliasedInt size)
            {
                Records = new Record[Capacity = capacity];
                Mask = capacity - 1;
                ReprobeLimit = (capacity >> REPROBE_SHIFT) + REPROBE_LOWER_BOUND;
                if(ReprobeLimit > capacity)
                    ReprobeLimit = capacity;
                PrevSize = Size = size;
            }
        }
        
        private Table _table;
        private readonly int _initialCapacity;
        private readonly IEqualityComparer<T> _cmp;
        /// <summary>The capacity used with those constructors that do not take a capacity parameter.
        /// </summary>
        public static readonly int DefaultCapacity = 1;
        /// <summary>Creates a new lock-free set.
        /// </summary>
        /// <param name="capacity">The initial capacity of the set.</param>
        /// <param name="comparer">An <see cref="IEqualityComparer&lt;TKey>" /> that compares the items.</param>
        public LockFreeSet(int capacity, IEqualityComparer<T> comparer)
        {
        	if(capacity < 0 || capacity > 0x4000000)
        		throw new ArgumentOutOfRangeException("capacity");
        	if(comparer == null)
        		throw new ArgumentNullException("comparer");
        	if(capacity == 0)
        		capacity = DefaultCapacity;
        	else
        	{
	            unchecked // binary round-up
	            {
	                --capacity;
	                capacity |= (capacity >> 1);
	                capacity |= (capacity >> 2);
	                capacity |= (capacity >> 4);
	                capacity |= (capacity >> 8);
	                capacity |= (capacity >> 16);
	                ++capacity;
	            }
        	}
            	
            _table = new Table(_initialCapacity = capacity, new AliasedInt());
            _cmp = comparer;
        }
        /// <summary>Creates a new lock-free set.
        /// </summary>
        /// <param name="capacity">The initial capacity of the set.</param>
        public LockFreeSet(int capacity)
            :this(capacity, EqualityComparer<T>.Default){}
        /// <summary>Creates a new lock-free set.
        /// </summary>
        /// <param name="comparer">An <see cref="IEqualityComparer&lt;TKey>" /> that compares the items.</param>
        public LockFreeSet(IEqualityComparer<T> comparer)
            :this(DefaultCapacity, comparer){}
        /// <summary>Creates a new lock-free set.
        /// </summary>
        public LockFreeSet()
            :this(DefaultCapacity){}
        private static int EstimateNecessaryCapacity(IEnumerable<T> collection)
        {
        	if(collection == null)
        		throw new ArgumentNullException("collection", "Cannot create a new lock-free set from a null source collection");
        	ICollection<T> colKVP = collection as ICollection<T>;
        	if(colKVP != null)
        		return colKVP.Count;
        	ICollection col = collection as ICollection;
        	if(col != null)
        		return col.Count;
        	return DefaultCapacity;
        }
        /// <summary>Creates a new lock-free set.
        /// </summary>
        /// <param name="collection">An <see cref="IEnumerable&lt;T>"/> from which the set is filled upon creation.</param>
        /// <param name="comparer">An <see cref="IEqualityComparer&lt;TKey>"/> that compares the items.</param>
        public LockFreeSet(IEnumerable<T> collection, IEqualityComparer<T> comparer)
            :this(EstimateNecessaryCapacity(collection), comparer)
        {
            foreach(T item in collection)
                Add(item);
        }
        /// <summary>Creates a new lock-free set.
        /// </summary>
        /// <param name="collection">An <see cref="IEnumerable&lt;T>"/> from which the set is filled upon creation.</param>
        public LockFreeSet(IEnumerable<T> collection)
            :this(collection, EqualityComparer<T>.Default){}
        [SecurityPermission(SecurityAction.Demand, SerializationFormatter=true)]
        void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("ic", _initialCapacity);
            info.AddValue("cmp", _cmp, typeof(IEqualityComparer<T>));
            int cItems = 0;
            foreach(Box box in EnumerateBoxes())
                info.AddValue("i" + cItems++, box.Value, typeof(T));
            info.AddValue("c", cItems);
        }
        private LockFreeSet(SerializationInfo info, StreamingContext context)
            :this(info.GetInt32("c"), (IEqualityComparer<T>)info.GetValue("cmp", typeof(IEqualityComparer<T>)))
        {
            _initialCapacity = info.GetInt32("ic");
            int count = info.GetInt32("c");
            if(count < 0)
                throw new SerializationException();
            for(int i = 0; i != count; ++i)
                this.Add((T)info.GetValue("i" + i, typeof(T)));
        }
        private int Hash(T item)
        {
            //We must prohibit the value of zero in order to be sure that when we encounter a
            //zero, that the hash has not been written.
            //We do not use a Wang-Jenkins like Dr. Click’s approach, since .NET’s IComparer allows
            //users of the class to fix the effects of poor hash algorithms.
            int givenHash = _cmp.GetHashCode(item);
            return givenHash == 0 ? ZERO_HASH : givenHash;
        }
        private bool Obtain(T item, out T storedItem)
        {
            return Obtain(_table, item, Hash(item), out storedItem);
        }
        private bool Obtain(Table table, T item, int hash, out T storedItem)
        {
            int idx = hash & table.Mask;
            int reprobeCount = 0;
            int maxProbe = table.ReprobeLimit;
            Record[] records = table.Records;
            for(;;)
            {
                int curHash = records[idx].Hash;
                if(curHash  == 0)// nothing written yet
                {
                    Table next = table.Next;
                    if(next != null)
                        return Obtain(next, item, hash, out storedItem);
                    storedItem = default(T);
                    return false;
                }
                Box box = records[idx].Box;
                if(curHash == hash)//hash we care about, is it the item we care about?
                {
                    if(_cmp.Equals(item, box.Value))//items match, and this can’t change
                    {
                        PrimeBox asPrime = box as PrimeBox;
                        if(asPrime != null)
                        {
                            CopySlotsAndCheck(table, asPrime, idx);
                            return Obtain(table.Next, item, hash, out storedItem);
                        }
                        else if(box is TombstoneBox)
                        {
                            storedItem = default(T);
                            return false;
                        }
                        else
                        {
                            storedItem = box.Value;
                            return true;
                        }
                    }
                }
                if(++reprobeCount >= maxProbe)
                {
                    Table next = table.Next;
                    if(next == null)
                    {
                        storedItem = default(T);
                        return false;
                    }
                    return Obtain(next, item, hash, out storedItem);
                }
                idx = (idx + 1) & table.Mask;
            }
        }
        private Box PutIfMatch(Box box, bool removing, bool emptyOnly)
        {
            return PutIfMatch(_table, box, Hash(box.Value), removing, emptyOnly);
        }
        private Box PutIfMatch(Table table, Box box, int hash, bool removing, bool emptyOnly)
        {
            int mask = table.Mask;
            int idx = hash & mask;
            int reprobeCount = 0;
            int maxProbe = table.ReprobeLimit;
            Record[] records = table.Records;
            Box curBox;
            for(;;)
            {
                int curHash = records[idx].Hash;
                if(curHash == 0)//nothing written here
                {
                    if(box is TombstoneBox)
                        return null;//don’t change anything
                    if((curHash = Interlocked.CompareExchange(ref records[idx].Hash, hash, 0)) == 0)
                        curHash = hash;
                    //now fallthrough to the next check, which we will pass if the above worked
                    //or if another thread happened to write the same hash we wanted to write
                }
                if(curHash == hash)
                {
                    //hashes match, do items?
                    //while retrieving the current
                    //if we want to write to empty records
                    //let’s see if we can just write because there’s nothing there...
                    if(!removing)
                    {
                        if((curBox = Interlocked.CompareExchange(ref records[idx].Box, box, null)) == null)
                        {
                            table.Slots.Increment();
                            if(!emptyOnly)
                                table.Size.Increment();
                            return null;
                        }
                    }
                    else
                        curBox = records[idx].Box;
                    //okay there’s something with the same hash here, does it have the same item?
                    if(_cmp.Equals(curBox.Value, box.Value))
                        break;
                }
                else
                    curBox = records[idx].Box; //just to check for dead records
                if(curBox == Box.DeadItem || ++reprobeCount >= maxProbe)
                {
                    Table next = table.Next ?? Resize(table);
                    //test if we’re putting from a copy
                    //and don’t do this if that’s
                    //the case
                    PrimeBox prevPrime = curBox as PrimeBox ?? new PrimeBox(curBox.Value);
                    HelpCopy(table, prevPrime, false);
                    return PutIfMatch(next, box, hash, removing, emptyOnly);
                }
                idx = (idx + 1) & mask;
            }
            //we have a record with a matching key.
            if((box is TombstoneBox) == (curBox is TombstoneBox))
                return curBox;//no change, return that stored.
            
            if(table.Next != null)
            {
                PrimeBox prevPrime = curBox as PrimeBox ?? new PrimeBox(curBox.Value);
                CopySlotsAndCheck(table, prevPrime, idx);
                HelpCopy(table, prevPrime, false);
                return PutIfMatch(table.Next, box, hash, removing, emptyOnly);
            }
            for(;;)
            {
                Box prevBox = Interlocked.CompareExchange(ref records[idx].Box, box, curBox);
                if(prevBox == curBox)
                {
                    if(!emptyOnly)
                    {
                        if(box is TombstoneBox)
                        {
                            if(!(prevBox is TombstoneBox))
                               table.Size.Decrement();
                        }
                        else if(prevBox is TombstoneBox)
                            table.Size.Increment();
                    }
                    return prevBox;
                }
                //we lost the race, another thread set the box.
                PrimeBox prevPrime = prevBox as PrimeBox;
                if(prevPrime != null)
                {
                    CopySlotsAndCheck(table, prevPrime, idx);
                    if(!emptyOnly)
                        HelpCopy(table, prevPrime, false);
                    return PutIfMatch(table.Next, box, hash, removing, emptyOnly);
                }
                else if(prevBox == Box.DeadItem)
                    return PutIfMatch(table.Next, box, hash, removing, emptyOnly);
                else if((box is TombstoneBox) == (prevBox is TombstoneBox))
                    return prevBox;//no change, return that stored.
                curBox = prevBox;
            }
        }
        private void CopySlotsAndCheck(Table table, PrimeBox prime, int idx)
        {
            if(CopySlot(table, prime, idx))
                CopySlotAndPromote(table, 1);
        }
        private void HelpCopy(Table table, PrimeBox prime, bool all)
        {
            int chunk = table.Capacity;
            if(chunk > 1024)
                chunk = 1024;
            while(table.CopyDone < table.Capacity)
            {
                int copyIdx = Interlocked.Add(ref table.CopyIdx, chunk) & table.Mask;
                int workDone = 0;
                for(int i = 0; i != chunk; ++i)
                    if(CopySlot(table, prime, copyIdx + i))
                        ++workDone;
                if(workDone != 0)
                    CopySlotAndPromote(table, workDone);
                if(!all)
                    return;
            }
        }
        private void CopySlotAndPromote(Table table, int workDone)
        {
            if(Interlocked.Add(ref table.CopyDone, workDone) >= table.Capacity && table == _table)
                while(Interlocked.CompareExchange(ref _table, table.Next, table) == table)
                {
                    table = _table;
                    if(table.CopyDone < table.Capacity)
                        break;
                }
        }
        private bool CopySlot(Table table, PrimeBox prime, int idx)
        {
            Record[] records = table.Records;
            //if unwritten-to we should be able to just mark it as dead.
            if(records[idx].Hash == 0 && Interlocked.CompareExchange(ref records[idx].Box, Box.DeadItem, null) == null)
                return true;
            Box box = records[idx].Box;
            Box oldBox = box;
            while(!(box is PrimeBox))
            {
                if(box is TombstoneBox)
                {
                    oldBox = Interlocked.CompareExchange(ref records[idx].Box, Box.DeadItem, box);
                    if(oldBox == box)
                        return true;
                }
                else
                {
                    box.FillPrime(prime);
                    oldBox = Interlocked.CompareExchange(ref records[idx].Box, prime, box);
                    if(box == oldBox)
                    {
                        if(box is TombstoneBox)
                            return true;
                        box = prime;
                        break;
                    }
                }
                box = oldBox;
            }
            if(box is TombstoneBox)
                return false;
            
            Box newBox = oldBox.StripPrime();
            
            bool copied = PutIfMatch(table.Next, newBox, records[idx].Hash, false, true) == null;
            
            while((oldBox = Interlocked.CompareExchange(ref records[idx].Box, Box.DeadItem, box)) != box)
                box = oldBox;
            
            return copied;
        }
        private Table Resize(Table tab)
        {
            int sz = tab.Size;
            int cap = tab.Capacity;
            Table next = tab.Next;
            if(next != null)
                return next;
            int newCap;
            if(sz >= cap * 3 / 4)
                newCap = sz * 8;
            else if(sz >= cap / 2)
                newCap = sz * 4;
            else if(sz >= cap / 4)
                newCap = sz * 2;
            else
                newCap = sz;
         	if(tab.Slots >= sz << 1)
                newCap = cap << 1;
            if(newCap < cap)
                newCap = cap;
            if(sz == tab.PrevSize)
                newCap *= 2;

            unchecked // binary round-up
            {
                --newCap;
                newCap |= (newCap >> 1);
                newCap |= (newCap >> 2);
                newCap |= (newCap >> 4);
                newCap |= (newCap >> 8);
                newCap |= (newCap >> 16);
                ++newCap;
            }
            
            int resizers = Interlocked.Increment(ref tab.Resizers);
            int MB = newCap / 0x40000;
            if(MB > 0 && resizers > 2)
            {
                if((next = tab.Next) != null)
                    return next;
                Thread.SpinWait(20);
                if((next = tab.Next) != null)
                    return next;
                Thread.Sleep(Math.Max(MB * 5 * resizers, 200));
            }
            
            if((next = tab.Next) != null)
                return next;
            
            next = new Table(newCap, tab.Size);

            #pragma warning disable 420 // CompareExchange has its own volatility guarantees
            return Interlocked.CompareExchange(ref tab.Next, next, null) ?? next;
			#pragma warning restore 420
        }
        /// <summary>Returns an estimate of the current number of items in the set.
        /// </summary>
        public int Count
        {
            get { return _table.Size; }
        }
        /// <summary>
        /// The current capacity of the set.
        /// </summary>
        /// <remarks>If the set is in the midst of a resize, the capacity it is resizing to is returned, ignoring other internal storage in use.</remarks>
        public int Capacity
        {
            get { return _table.Capacity; }
        }
        bool ICollection<T>.IsReadOnly
        {
            get { return false; }
        }
        /// <summary>Attempts to add an item to the set.</summary>
        /// <param name="item">The item to add.</param>
        /// <returns>True if the item was added, false if a matching item was already present.</returns>
        public bool Add(T item)
        {
            Box prev = PutIfMatch(new Box(item), false, false);
            return prev != null || !(prev is TombstoneBox);
        }
        /// <summary>Attempts to add a collection of items to the set, returning those which were added.</summary>
        /// <param name="items">The items to add.</param>
        /// <returns>An enumeration of those items which where added to the set, excluding those which were already present.</returns>
        /// <remarks>The returned enumerable is lazily executed, and items are only added to the set as it is enumerated.</remarks>
        public IEnumerable<T> FilterAdd(IEnumerable<T> items)
        {
            foreach(T item in items)
                if(Add(item))
                    yield return item;
        }
        /// <summary>Attempts to add a collection of items to the set, returning the number added.</summary>
        /// <param name="items">The items to add.</param>
        /// <returns>The number of items added, excluding those which were already present.</returns>
        public int AddRange(IEnumerable<T> items)
        {
            int count = 0;
            foreach(T item in FilterAdd(items))
                ++count;
            return count;
        }
        /// <summary>Retrieves a reference to the specified item.</summary>
        /// <param name="item">The item sought.</param>
        /// <returns>A reference to a matching item if it is present in the set, null otherwise.</returns>
        /// <exception cref="System.InvalidOperationException"> An attempt was made to use this when the generic type of the
        /// set is not a reference type (that is, a value or pointer type).</exception>
        /// <remarks>This allows use of the set to restrain a group of objects to exclude duplicates, allowing for reduced
        /// memory use, and reference-based equality checking, comparable with how <see cref="string.IsInterned(string)"/> allows
        /// one to check for a copy of a string in the CLR intern pool, but also allowing for removal, clearing and multiple pools. This is clearly
        /// only valid for reference types.</remarks>
        public T Find(T item)
        {
            if(typeof(T).IsValueType || typeof(T).IsPointer)
                throw new InvalidOperationException("Retrieving stored reference is only valid for reference types");
            T found;
            return Obtain(item, out found) ? found : default(T);
        }
        /// <summary>Retrieves a reference to the specified item, adding it if necessary.</summary>
        /// <param name="item">The item sought.</param>
        /// <returns>A reference to a matching item if it is present in the set, using the item given if there isn’t
        /// already a matching item.</returns>
        /// <exception cref="System.InvalidOperationException"> An attempt was made to use this when the generic type of the
        /// set is not a reference type (that is, a value or pointer type).</exception>
        /// <remarks>This allows use of the set to restrain a group of objects to exclude duplicates, allowing for reduced
        /// memory use, and reference-based equality checking, comparable with how <see cref="string.Intern(string)"/> allows
        /// one to check for a copy of a string in the CLR intern pool, but also allowing for removal, clearing and multiple pools. This is clearly
        /// only valid for reference types.</remarks>
        public T FindOrStore(T item)
        {
            Box found = PutIfMatch(new Box(item), false, false);
            return found == null || found is TombstoneBox ? item : found.Value;
        }
        /// <summary>Modifies the current set so that it contains all elements that are present in both the current set and in the specified collection.</summary>
        /// <param name="other">The collection to compare to the current set.</param>
        /// <remarks>As this method will operate without locking, additions and removals from other threads may result in inconsistent results. For most
        /// purposes it will be only be useful while the collection is being operated upon by only one thread (perhaps before or after unlocked multi-threaded
        /// use).</remarks>
        public void UnionWith(IEnumerable<T> other)
        {
            if(other == null)
                throw new ArgumentNullException("other");
            if(other != this)
                foreach(T item in other)
                    Add(item);
        }
        /// <summary>Modifies the current set so that it contains only elements that are also in a specified collection.</summary>
        /// <param name="other">The collection to compare to the current set.</param>
        /// <remarks>As this method will operate without locking, additions and removals from other threads may result in inconsistent results. For most
        /// purposes it will be only be useful while the collection is being operated upon by only one thread (perhaps before or after unlocked multi-threaded
        /// use).</remarks>
        public void IntersectWith(IEnumerable<T> other)
        {
            if(other == null)
                throw new ArgumentNullException("other");
            if(other != this && Count != 0)
            {
                LockFreeSet<T> copyTo = new LockFreeSet<T>(Capacity, _cmp);
                foreach(T item in other)
                    if(Contains(item))
                        copyTo.Add(item);
                Thread.MemoryBarrier();
                _table = copyTo._table;
            }
        }
        /// <summary>Removes all elements in the specified collection from the current set.</summary>
        /// <param name="other">The collection to compare to the current set.</param>
        /// <remarks>As this method will operate without locking, additions and removals from other threads may result in inconsistent results. For most
        /// purposes it will be only be useful while the collection is being operated upon by only one thread (perhaps before or after unlocked multi-threaded
        /// use).</remarks>
        public void ExceptWith(IEnumerable<T> other)
        {
            if(other == null)
                throw new ArgumentNullException("other");
            else if(other == this)
                Clear();
            else
                foreach(T item in other)
                    Remove(item);
        }
        /// <summary>Modifies the current set so that it contains only elements that are present either in the current set or in the specified collection, but not both.</summary>
        /// <param name="other">The collection to compare to the current set.</param>
        /// <remarks>As this method will operate without locking, additions and removals from other threads may result in inconsistent results. For most
        /// purposes it will be only be useful while the collection is being operated upon by only one thread (perhaps before or after unlocked multi-threaded
        /// use).</remarks>
        public void SymmetricExceptWith(IEnumerable<T> other)
        {
            if(other == null)
                throw new ArgumentNullException("other");
            else if(other == this)
                Clear();
            else if(Count == 0)
                UnionWith(other);
            else
                foreach(T item in other)
                    if(!Remove(item))
                        Add(item);
        }
        /// <summary>Determines whether a set is a subset of a specified collection.</summary>
        /// <param name="other">The collection to compare to the current set.</param>
        /// <remarks>As this method will operate without locking, additions and removals from other threads may result in inconsistent results. For most
        /// purposes it will be only be useful while the collection is being operated upon by only one thread (perhaps before or after unlocked multi-threaded
        /// use).</remarks>
        public bool IsSubsetOf(IEnumerable<T> other)
        {
            if(other == null)
                throw new ArgumentNullException("other");
            int count = Count;
            if(count == 0)
                return true;
            ICollection<T> asCol = other as ICollection<T>;
            if(asCol != null)
            {
                if(asCol.Count < count)
                    return false;
                LockFreeSet<T> asLFHS = other as LockFreeSet<T>;
                if(asLFHS != null && asLFHS._cmp.Equals(_cmp))
                    return asLFHS.IsSupersetOf(this);
            }
            int cBoth = 0;
            foreach(T item in other)
                if(Contains(item))
                    ++cBoth;
            return cBoth == count;
        }
        /// <summary>Determines whether the current set is a superset of a specified collection.</summary>
        /// <param name="other">The collection to compare to the current set.</param>
        /// <remarks>As this method will operate without locking, additions and removals from other threads may result in inconsistent results. For most
        /// purposes it will be only be useful while the collection is being operated upon by only one thread (perhaps before or after unlocked multi-threaded
        /// use).</remarks>
        public bool IsSupersetOf(IEnumerable<T> other)
        {
            if(other == null)
                throw new ArgumentNullException("other");
            ICollection<T> asCol = other as ICollection<T>;
            if(asCol != null)
            {
                if(asCol.Count == 0)
                    return true;
                //We can only short-cut on other being larger if larger is a set
                //with the same equality comparer, as otherwise two or more items
                //could be considered a single item to this set.
                LockFreeSet<T> asLFHS = other as LockFreeSet<T>;
                if(asLFHS != null && _cmp.Equals(asLFHS._cmp) && asLFHS.Count > Count)
                    return false;
                HashSet<T> asHS = other as HashSet<T>;
                if(asHS != null && _cmp.Equals(asHS.Comparer) && asHS.Count > Count)
                    return false;
            }
            foreach(T item in other)
                if(!Contains(item))
                    return false;
            return true;
        }
        /// <summary>Determines whether the current set is a correct superset of a specified collection.</summary>
        /// <param name="other">The collection to compare to the current set.</param>
        /// <remarks>As this method will operate without locking, additions and removals from other threads may result in inconsistent results. For most
        /// purposes it will be only be useful while the collection is being operated upon by only one thread (perhaps before or after unlocked multi-threaded
        /// use).</remarks>
        public bool IsProperSupersetOf(IEnumerable<T> other)
        {
            if(other == null)
                throw new ArgumentNullException("other");
            ICollection<T> asCol = other as ICollection<T>;
            if(asCol != null)
            {
                if(asCol.Count == 0)
                    return true;
                //We can only short-cut on other being larger if larger is a set
                //with the same equality comparer, as otherwise two or more items
                //could be considered a single item to this set.
                LockFreeSet<T> asLFHS = other as LockFreeSet<T>;
                if(asLFHS != null && _cmp.Equals(asLFHS._cmp) && asLFHS.Count > Count)
                    return false;
                HashSet<T> asHS = other as HashSet<T>;
                if(asHS != null && _cmp.Equals(asHS.Comparer) && asHS.Count > Count)
                    return false;
            }
            int matched = 0;
            foreach(T item in other)
                if(Contains(item))
                    ++matched;
                else
                    return false;
            return matched < Count;
        }
        /// <summary>Determines whether the current set is a property (strict) subset of a specified collection.</summary>
        /// <param name="other">The collection to compare to the current set.</param>
        /// <remarks>As this method will operate without locking, additions and removals from other threads may result in inconsistent results. For most
        /// purposes it will be only be useful while the collection is being operated upon by only one thread (perhaps before or after unlocked multi-threaded
        /// use).</remarks>
        public bool IsProperSubsetOf(IEnumerable<T> other)
        {
            if(other == null)
                throw new ArgumentNullException("other");
            int count = Count;
            if(count == 0)
                return true;
            ICollection<T> asCol = other as ICollection<T>;
            if(asCol != null)
            {
                if(asCol.Count < count)
                    return false;
                LockFreeSet<T> asLFHS = other as LockFreeSet<T>;
                if(asLFHS != null && asLFHS._cmp.Equals(_cmp))
                    return asLFHS.IsProperSupersetOf(this);
            }
            int cBoth = 0;
            bool notInThis = false;
            foreach(T item in other)
                if(Contains(item))
                    ++cBoth;
                else
                    notInThis = true;
            return notInThis && cBoth == count;
        }
        /// <summary>Determines whether the current set overlaps with the specified collection.</summary>
        /// <param name="other">The collection to compare to the current set.</param>
        /// <remarks>As this method will operate without locking, additions and removals from other threads may result in inconsistent results. For most
        /// purposes it will be only be useful while the collection is being operated upon by only one thread (perhaps before or after unlocked multi-threaded
        /// use).</remarks>
        public bool Overlaps(IEnumerable<T> other)
        {
            if(other == null)
                throw new ArgumentNullException("other");
            if(Count != 0)
                foreach(T item in other)
                    if(Contains(item))
                        return true;
            return false;
        }
        /// <summary>Determines whether the current set and the specified collection contain the same elements.</summary>
        /// <param name="other">The collection to compare to the current set.</param>
        /// <remarks>As this method will operate without locking, additions and removals from other threads may result in inconsistent results. For most
        /// purposes it will be only be useful while the collection is being operated upon by only one thread (perhaps before or after unlocked multi-threaded
        /// use).</remarks>
        public bool SetEquals(IEnumerable<T> other)
        {
            if(other == null)
                throw new ArgumentNullException("other");
            int asSetCount = -1;
            LockFreeSet<T> asLFHS = other as LockFreeSet<T>;
            if(asLFHS != null && _cmp.Equals(asLFHS._cmp) && asLFHS.Count > Count)
                asSetCount = asLFHS.Count;
            else
            {
                HashSet<T> asHS = other as HashSet<T>;
                if(asHS != null && _cmp.Equals(asHS.Comparer) && asHS.Count > Count)
                    asSetCount = asHS.Count;
            }
            if(asSetCount != -1)
            {
                if(Count != asSetCount)
                    return false;
                foreach(T item in other)
                    if(!Contains(item))
                        return false;
                return true;
            }
            int matched = 0;
            foreach(T item in other)
                if(Contains(item))
                    ++matched;
                else
                    return false;
            return matched == Count;
        }
        void ICollection<T>.Add(T item)
        {
            Add(item);
        }
        /// <summary>
        /// Removes all items from the set.
        /// </summary>
        /// <remarks>All items are removed in a single atomic operation.</remarks>
        public void Clear()
        {
            Thread.MemoryBarrier();
            _table = new Table(_initialCapacity, new AliasedInt());
        }
        /// <summary>
        /// Determines whether an item is present in the set.
        /// </summary>
        /// <param name="item">The item sought.</param>
        /// <returns>True if the item is found, false otherwise.</returns>
        public bool Contains(T item)
        {
            T found;
            return Obtain(item, out found);
        }
        /// <summary>
        /// Copies the contents of the set to an array.
        /// </summary>
        /// <param name="array">The array to copy to.</param>
        /// <param name="arrayIndex">The index within the array to start copying from</param>
        /// <exception cref="System.ArgumentNullException"/>The array was null.
        /// <exception cref="System.ArgumentOutOfRangeException"/>The array index was less than zero.
        /// <exception cref="System.ArgumentException"/>The number of items in the collection was
        /// too great to copy into the array at the index given.
        public void CopyTo(T[] array, int arrayIndex)
        {
            if(array == null)
                throw new ArgumentNullException("array");
            if(arrayIndex < 0)
                throw new ArgumentOutOfRangeException("arrayIndex");
            ToHashSet().CopyTo(array, arrayIndex);
        }
        /// <summary>
        /// Copies the contents of the set to an array.
        /// </summary>
        /// <param name="array">The array to copy to.</param>
        /// <exception cref="System.ArgumentNullException"/>The array was null.
        /// <exception cref="System.ArgumentException"/>The number of items in the collection was
        /// too great to copy into the array.
        public void CopyTo(T[] array)
        {
            CopyTo(array, 0);
        }
        /// <summary>
        /// Removes an item from the set.
        /// </summary>
        /// <param name="item">The item to remove.</param>
        /// <returns>True if the item was removed, false if it was not found.</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.</remarks>
        public bool Remove(T item)
        {
            T dummy;
            return Remove(item, out dummy);
        }
        /// <summary>
        /// Removes an item from the set.
        /// </summary>
        /// <param name="item">The item to remove.</param>
        /// <param name="removed">Upon returning, the item removed.</param>
        /// <returns>True if an item was removed, false if no matching item was found.</returns>
        public bool Remove(T item, out T removed)
        {
            Box prev = PutIfMatch(new TombstoneBox(item), true, false);
            if(prev == null || prev is TombstoneBox)
            {
                removed = default(T);
                return false;
            }
            removed = prev.Value;
            return true;
        }
        /// <summary>
        /// Removes items from the set that match a predicate.
        /// </summary>
        /// <param name="predicate">A <see cref="System.Func&lt;T, TResult>"/> that returns true for the items that should be removed.</param>
        /// <returns>A <see cref="System.Collections.Generic.IEnumerable&lt;T>"/> of the items removed.</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.
        /// <para>The returned enumerable is lazily executed, and items are only removed from the dictionary as it is enumerated.</para></remarks>
        public IEnumerable<T> RemoveWhere(Func<T, bool> predicate)
        {
            if(predicate == null)
                throw new ArgumentNullException("predicate");
            int removed = 0;
            Table table = _table;
            for(;;)
            {
                removed = 0;
                Record[] records = table.Records;
                for(int idx = 0; idx != records.Length; ++idx)
                {
                    Record record = records[idx];
                    Box box = record.Box;
                    PrimeBox prime = box as PrimeBox;
                    if(prime != null)
                        CopySlotsAndCheck(table, prime, idx);
                    else if(box != null && !(box is TombstoneBox) && predicate(box.Value))
                    {
                        TombstoneBox tomb = new TombstoneBox(box.Value);
                        for(;;)
                        {
                            Box oldBox = Interlocked.CompareExchange(ref records[idx].Box, tomb, box);
                            if(oldBox == box)
                            {
                                table.Size.Decrement();
                                yield return oldBox.Value;
                                ++removed;
                                break;
                            }
                            else if(oldBox is PrimeBox)
                                CopySlotsAndCheck(table, (PrimeBox)oldBox, idx);
                            else if(oldBox is TombstoneBox || !predicate(oldBox.Value))
                                break;
                            else
                                box = oldBox;
                        }
                    }
                }
                Table next = table.Next;
                if(next != null)
                    table = next;
                else
                {
                    if(removed > Capacity >> 4 || removed > Count >> 2)
                        Resize(table);
                    yield break;
                }
            }
        }
        /// <summary>Removes all items that match a predicate.
        /// </summary>
        /// <param name="predicate">A <see cref="System.Func&lt;T, TResult>"/> that returns true when passed an item that should be removed.</param>
        /// <returns>The number of items removed</returns>
        /// <remarks>Removal internally requires an allocation. This is generally negliable, but it should be noted
        /// that <see cref="System.OutOfMemoryException"/> exceptions are possible in memory-critical situations.</remarks>
        public int Remove(Func<T, bool> predicate)
        {
            int total = 0;
            foreach(T item in RemoveWhere(predicate))
                ++total;
            return total;
        }
        internal class BoxEnumerator : IEnumerable<Box>, IEnumerator<Box>
        {
            private readonly LockFreeSet<T> _set;
            private Table _tab;
            private Box _current;
            private int _idx = -1;
            public BoxEnumerator(LockFreeSet<T> lfhs)
            {
                _tab = (_set = lfhs)._table;
            }
            public BoxEnumerator GetEnumerator()
            {
                return this;
            }
            IEnumerator<Box> IEnumerable<Box>.GetEnumerator()
            {
                return this;
            }
            IEnumerator IEnumerable.GetEnumerator()
            {
                return this;
            }
            public Box Current
            {
                get { return _current; }
            }
            
            object IEnumerator.Current
            {
                get { return Current; }
            }
            
            void IDisposable.Dispose()
            {
                //
            }
            
            public bool MoveNext()
            {
                for(; _tab != null; _tab = _tab.Next, _idx = -1)
                {
                    Record[] records = _tab.Records;
                    for(++_idx; _idx != records.Length; ++_idx)
                    {
                        Box box = records[_idx].Box;
                        if(box != null && !(box is TombstoneBox))
                        {
                            PrimeBox prime = box as PrimeBox;
                            if(prime != null)//part-way through being copied to next table
                                _set.CopySlotsAndCheck(_tab, prime, _idx);//make sure it’s there when we come to it.
                            else
                            {
                                _current = box;
                                return true;
                            }
                        }
                    }
                }
                return false;
            }
            
            public void Reset()
            {
                _tab = _set._table;
                _idx = -1;
            }
        }
        /// <summary>Enumerates a LockFreeSet&lt;T>.
        /// </summary>
        /// <remarks>The use of a value type for <see cref="System.Collections.Generic.List&lt;T>.Enumerator"/> has drawn some criticism.
        /// Note that this does not apply here, as the state that changes with enumeration is not maintained by the structure itself.</remarks>
        public struct Enumerator : IEnumerator<T>
        {
            private BoxEnumerator _src;
            internal Enumerator(BoxEnumerator src)
            {
                _src = src;
            }
            /// <summary>
            /// Returns the current item being enumerated.
            /// </summary>
            public T Current
            {
                get { return _src.Current.Value; }
            }
            object IEnumerator.Current
            {
                get { return Current; }
            }
            void IDisposable.Dispose()
            {
            }
            /// <summary>
            /// Moves to the next item in the enumeration.
            /// </summary>
            /// <returns>True if another item was found, false if the end of the enumeration was reached.</returns>
            public bool MoveNext()
            {
                return _src.MoveNext();
            }
            /// <summary>
            /// Reset the enumeration
            /// </summary>
            public void Reset()
            {
                _src.Reset();
            }
        }
        private BoxEnumerator EnumerateBoxes()
        {
            return new BoxEnumerator(this);
        }
        /// <summary>Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public Enumerator GetEnumerator()
        {
            return new Enumerator(EnumerateBoxes());
        }
        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    	/// <summary>
    	/// Returns a copy of the current set.
    	/// </summary>
        /// <remarks>Because this operation does not lock, the resulting set’s contents
        /// could be inconsistent in terms of an application’s use of the values.
        /// <para>If there is a value stored with a null key, it is ignored.</para></remarks>
        /// <returns>The <see cref="LockFreeSet&lt;T>"/>.</returns>
        public LockFreeSet<T> Clone()
        {
            LockFreeSet<T> copy = new LockFreeSet<T>(Capacity, _cmp);
            foreach(Box box in EnumerateBoxes())
                copy.PutIfMatch(box, false, false);
            return copy;
        }
        object ICloneable.Clone()
        {
            return Clone();
        }
        /// <summary>
        /// Returns a <see cref="HashSet&lt;T>"/> with the same contents and equality comparer as
        /// the lock-free set.
        /// </summary>
        /// <returns>The HashSet.</returns>
        /// <remarks>Because this operation does not lock, the resulting set’s contents
        /// could be inconsistent in terms of an application’s use of the values.</remarks>
        public HashSet<T> ToHashSet()
        {
            return new HashSet<T>(this, _cmp);
        }
        /// <summary>
        /// Returns a <see cref="List&lt;T>"/> with the same contents as
        /// the lock-free set.
        /// </summary>
        /// <returns>The List.</returns>
        /// <remarks>Because this operation does not lock, the resulting set’s contents
        /// could be inconsistent in terms of an application’s use of the values, or include duplicate items</remarks>
        public List<T> ToList()
        {
            return new List<T>(ToHashSet());
        }
        /// <summary>
        /// Returns an array with the same contents as
        /// the lock-free set.
        /// </summary>
        /// <returns>The array.</returns>
        /// <remarks>Because this operation does not lock, the resulting set’s contents
        /// could be inconsistent in terms of an application’s use of the values, or include duplicate items</remarks>
        public T[] ToArray()
        {
            HashSet<T> hs = ToHashSet();
            T[] array = new T[hs.Count];
            int i = 0;
            foreach(T item in hs)
                array[i++] = item;
            return array;
        }
        
        object ICollection.SyncRoot
        {
            get { throw new NotSupportedException("SyncRoot property is not supported, and unnecesary with this class."); }
        }
        
        bool ICollection.IsSynchronized
        {
            get { return false; }
        }
        
        bool IProducerConsumerCollection<T>.TryAdd(T item)
        {
            return Add(item);
        }
        /// <summary>
        /// Attempts to take a single item from the set.
        /// </summary>
        /// <param name="item">On return, the item removed, if successful.</param>
        /// <returns>True if an item was removed, false if the set had been empty.</returns>
        /// <remarks>The item returned is arbitrarily determined, with no guaranteed ordering.</remarks>
        public bool TryTake(out T item)
        {
            for(Table table = _table; table != null; table = table.Next)
            {
                Record[] records = table.Records;
                for(int idx = 0; idx != records.Length; ++idx)
                {
                    Box curBox = records[idx].Box;
                    if(curBox != null && !(curBox is TombstoneBox))
                    {
                        PrimeBox prime = curBox as PrimeBox;
                        if(prime != null)
                        {
                            CopySlotsAndCheck(table, prime, idx);
                        }
                        else
                            for(;;)
                            {
                                Box prevBox = Interlocked.CompareExchange(ref records[idx].Box, new TombstoneBox(curBox.Value), curBox);
                                if(prevBox == curBox)
                                {
                                    item = curBox.Value;
                                    return true;
                                }
                                if(prevBox is TombstoneBox)
                                    break;
                                curBox = prevBox;
                            }
                    }
                }
            }
            item = default(T);
            return false;
        }
        void ICollection.CopyTo(Array array, int index)
        {
            throw new NotImplementedException();
        }
    }
}