using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.Excel
{
    [TestClass]
    public class OpenXmlHelperTests
    {
        [DataTestMethod]
        [DataRow("A1", 0)]
        [DataRow("B2", 1)]
        [DataRow("Z9", 25)]
        [DataRow("AA10", 26)]
        [DataRow("AB1", 27)]
        [DataRow("AZ1", 51)]
        [DataRow("BA1", 52)]
        [DataRow("XFD1048576", 16383)]
        [DataRow("c3", 2)]
        [DataRow("$D$4", 3)]
        [DataRow("C", 2)]
        public void GetColumnIndex_ParsesColumnLetters(string reference, int expected)
        {
            Assert.AreEqual(expected, OpenXmlHelper.GetColumnIndex(reference));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("12")]
        [DataRow("$")]
        public void GetColumnIndex_InvalidReference_ReturnsMinusOne(string reference)
        {
            Assert.AreEqual(-1, OpenXmlHelper.GetColumnIndex(reference));
        }

        [TestMethod]
        public void GetColumnIndex_IsInverseOfGetCellReference()
        {
            for (int col = 0; col < 1000; col++)
            {
                Assert.AreEqual(col, OpenXmlHelper.GetColumnIndex(OpenXmlHelper.GetCellReference(col, 4)));
            }
        }
    }
}
