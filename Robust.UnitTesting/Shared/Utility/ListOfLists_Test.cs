using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Robust.Shared.Utility;

namespace Robust.UnitTesting.Shared.Utility
{
    [Parallelizable(ParallelScope.All | ParallelScope.Fixtures)]
    [TestFixture]
    public class ListOfLists_Test
    {
        [Test]
        public void BasicTest()
        {
            var listA = new List<int> {1, 2, 3};
            var listB = new List<int> {4, 5, 6};

            var lol = new ListOfLists<int>(listA, listB);
            for (var i = 0; i < 6; i++) {
                Assert.That(lol[i], Is.EqualTo(i+1));
                Assert.That(lol.GetOwner(i), Is.EqualTo(i < 3 ? listA : listB));
            }
        }

        [Test]
        public void AddEnd()
        {
            var listA = new List<int> {1, 2, 3};
            var listB = new List<int> {4, 5, 6};

            var lol = new ListOfLists<int>(listA, listB);
            lol.Add(7);
            Assert.That(listA.Count, Is.EqualTo(3));
            Assert.That(listB.Count, Is.EqualTo(4));
        }

        [Test]
        public void InsertAt()
        {
            var listA = new List<int> {1, 2, 3};
            var listB = new List<int> {4, 5, 6};

            var lol = new ListOfLists<int>(listA, listB);
            lol.Insert(2, 7);
            Assert.That(listA.Count, Is.EqualTo(4));
            Assert.That(listB.Count, Is.EqualTo(3));
        }
    }
}
