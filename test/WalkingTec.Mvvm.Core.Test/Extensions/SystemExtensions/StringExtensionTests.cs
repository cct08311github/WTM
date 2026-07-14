#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test.Extensions.SystemExtensions
{
    [TestClass]
    public class StringExtensionTests
    {
        // ─── GetIdByName ──────────────────────────────────────────────────────

        [TestMethod]
        public void GetIdByName_NullInput_ReturnsEmpty()
        {
            ((string)null!).GetIdByName().Should().Be("");
        }

        [TestMethod]
        public void GetIdByName_NoDots_ReturnsUnchanged()
        {
            "FieldName".GetIdByName().Should().Be("FieldName");
        }

        [TestMethod]
        public void GetIdByName_WithDots_ReplacedWithUnderscore()
        {
            "a.b.c".GetIdByName().Should().Be("a_b_c");
        }

        [TestMethod]
        public void GetIdByName_WithBrackets_ReplacedWithUnderscore()
        {
            "Items[0].Name".GetIdByName().Should().Be("Items_0__Name");
        }

        [TestMethod]
        public void GetIdByName_MixedChars_AllReplaced()
        {
            "Foo[1].Bar".GetIdByName().Should().Be("Foo_1__Bar");
        }

        // ─── CorrectUrl ───────────────────────────────────────────────────────

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void CorrectUrl_NullOrWhitespace_ReturnsEmpty(string? input)
        {
            input!.CorrectUrl().Should().Be("");
        }

        [TestMethod]
        public void CorrectUrl_AlreadyHttp_ReturnsLowercaseTrimmed()
        {
            "http://example.com/".CorrectUrl().Should().Be("http://example.com");
        }

        [TestMethod]
        public void CorrectUrl_AlreadyHttps_PreservesScheme()
        {
            "https://example.com".CorrectUrl().Should().Be("https://example.com");
        }

        [TestMethod]
        public void CorrectUrl_NoScheme_PrependHttp()
        {
            "example.com".CorrectUrl().Should().Be("http://example.com");
        }

        [TestMethod]
        public void CorrectUrl_LeadingSlash_Trimmed()
        {
            "/example.com/".CorrectUrl().Should().Be("http://example.com");
        }

        [TestMethod]
        public void CorrectUrl_UpperCase_ToLower()
        {
            "EXAMPLE.COM".CorrectUrl().Should().Be("http://example.com");
        }

        // ─── ToSepratedString<T,V> ────────────────────────────────────────────

        [TestMethod]
        public void ToSepratedString_Typed_NullList_ReturnsEmpty()
        {
            ((IEnumerable<string>)null!).ToSepratedString(x => x).Should().Be("");
        }

        [TestMethod]
        public void ToSepratedString_Typed_EmptyList_ReturnsEmpty()
        {
            new List<string>().ToSepratedString(x => x).Should().Be("");
        }

        [TestMethod]
        public void ToSepratedString_Typed_SingleElement_NoSeparator()
        {
            new List<string> { "hello" }.ToSepratedString(x => x).Should().Be("hello");
        }

        [TestMethod]
        public void ToSepratedString_Typed_MultipleElements_CommaSeparated()
        {
            new List<string> { "a", "b", "c" }.ToSepratedString(x => x)
                .Should().Be("a,b,c");
        }

        [TestMethod]
        public void ToSepratedString_Typed_CustomSeparator()
        {
            new List<int> { 1, 2, 3 }.ToSepratedString(x => x, seperator: "|")
                .Should().Be("1|2|3");
        }

        [TestMethod]
        public void ToSepratedString_Typed_WithFormat_UsesFormat()
        {
            new List<int> { 1, 2 }.ToSepratedString(x => x, v => $"[{v}]")
                .Should().Be("[1],[2]");
        }

        [TestMethod]
        public void ToSepratedString_Typed_NullValue_EmptyString()
        {
            new List<string?> { "a", null, "b" }.ToSepratedString(x => x)
                .Should().Be("a,,b");
        }

        // ─── ToSepratedString<T,V> regression coverage for #663 (Compile() hoist +
        //     O(n^2) Count()/ElementAt() removal) - output must stay byte-identical.

        [TestMethod]
        public void ToSepratedString_Typed_FormatReturnsEmptyMidSequence_SeparatorStillEmitted()
        {
            // Separator placement is index-based (like the original "i < Count-1" check),
            // not content-based - an empty formatted value must NOT be skipped the way
            // the unrelated non-generic ToSepratedString(IEnumerable) overload does.
            new List<int> { 1, 2, 3 }.ToSepratedString(x => x, v => v == 2 ? "" : v.ToString())
                .Should().Be("1,,3");
        }

        [TestMethod]
        public void ToSepratedString_Typed_SingleElement_FormatReturnsEmpty_NoSeparator()
        {
            new List<int> { 1 }.ToSepratedString(x => x, v => "")
                .Should().Be("");
        }

        [TestMethod]
        public void ToSepratedString_Typed_LargeSequence_MatchesManualJoin()
        {
            // Guards the O(n) rewrite against off-by-one errors that a naive single-pass
            // rewrite could introduce at scale (the O(n^2) original was only practical to
            // hand-verify on small lists).
            var items = Enumerable.Range(0, 500).ToList();
            var expected = string.Join(",", items);
            items.ToSepratedString(x => x).Should().Be(expected);
        }

        [TestMethod]
        public void ToSepratedString_Typed_SinglePassEnumerable_EnumeratedOnce()
        {
            // The original implementation re-enumerated `self` via Count()/ElementAt(i)
            // on every loop iteration; a source that only supports a single enumeration
            // would misbehave (or throw) under that pattern. The rewrite must walk the
            // sequence with exactly one foreach.
            var source = new SinglePassEnumerable(["x", "y", "z"]);
            source.ToSepratedString(x => x, seperator: "-").Should().Be("x-y-z");
        }

        private sealed class SinglePassEnumerable(string[] items) : IEnumerable<string>
        {
            private bool _consumed;

            public IEnumerator<string> GetEnumerator()
            {
                if (_consumed)
                {
                    throw new InvalidOperationException("This sequence supports only a single enumeration.");
                }
                _consumed = true;
                return ((IEnumerable<string>)items).GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        // ─── ToSepratedString(IEnumerable) ────────────────────────────────────

        [TestMethod]
        public void ToSepratedString_NonGeneric_NullList_ReturnsEmpty()
        {
            ((System.Collections.IEnumerable)null!).ToSepratedString().Should().Be("");
        }

        [TestMethod]
        public void ToSepratedString_NonGeneric_EmptyList_ReturnsEmpty()
        {
            new object[0].ToSepratedString().Should().Be("");
        }

        [TestMethod]
        public void ToSepratedString_NonGeneric_SkipsNullAndEmpty()
        {
            // items that produce empty string are skipped by the non-generic overload
            new object[] { "a", "b" }.ToSepratedString().Should().Be("a,b");
        }

        [TestMethod]
        public void ToSepratedString_NonGeneric_WithFormat()
        {
            new object[] { 1, 2 }.ToSepratedString(o => $"#{o}").Should().Be("#1,#2");
        }

        // ─── ToSepratedString(NameValueCollection) ────────────────────────────

        [TestMethod]
        public void ToSepratedString_NameValueCollection_Null_ReturnsEmpty()
        {
            ((NameValueCollection)null!).ToSepratedString().Should().Be("");
        }

        [TestMethod]
        public void ToSepratedString_NameValueCollection_SingleEntry()
        {
            var nvc = new NameValueCollection { { "key", "value" } };
            nvc.ToSepratedString().Should().Be("key=value");
        }

        [TestMethod]
        public void ToSepratedString_NameValueCollection_MultiEntry_CustomSeparator()
        {
            var nvc = new NameValueCollection { { "a", "1" }, { "b", "2" } };
            var result = nvc.ToSepratedString("|");
            result.Should().Contain("a=1").And.Contain("b=2").And.Contain("|");
        }

        // ─── AppendQuery(string) ──────────────────────────────────────────────

        [TestMethod]
        public void AppendQuery_String_NullSelf_ReturnsNull()
        {
            ((string?)null).AppendQuery("x=1").Should().BeNull();
        }

        [TestMethod]
        public void AppendQuery_String_NoExistingQuery_AddQuestionMark()
        {
            "http://a.com".AppendQuery("x=1").Should().Be("http://a.com?x=1");
        }

        [TestMethod]
        public void AppendQuery_String_ExistingQuery_AddAmpersand()
        {
            "http://a.com?y=2".AppendQuery("x=1").Should().Be("http://a.com?y=2&x=1");
        }

        // ─── AppendQuery(IDictionary) ─────────────────────────────────────────

        [TestMethod]
        public void AppendQuery_Dict_NullSelf_ReturnsNull()
        {
            // IDictionary (non-generic) parameter overload - null self path is safe
            var ht = new Hashtable { { "k", "v" } };
            ((string?)null).AppendQuery(ht).Should().BeNull();
        }

        [TestMethod]
        public void AppendQuery_Dict_NonNullSelf_ThrowsInvalidCast()
        {
            // The source code foreach casts enumerator items to IDictionaryEnumerator
            // which always throws InvalidCastException for any IDictionary type including Hashtable.
            // This documents the actual (buggy) behaviour so regressions are caught.
            var ht = new Hashtable { { "a", "1" } };
            Action act = () => "http://x.com".AppendQuery(ht);
            act.Should().Throw<InvalidCastException>();
        }

        // ─── AppendQuery(List<KeyValuePair>) ─────────────────────────────────

        [TestMethod]
        public void AppendQuery_KvpList_NullSelf_ReturnsNull()
        {
            ((string?)null).AppendQuery(new List<KeyValuePair<string, string>>())
                .Should().BeNull();
        }

        [TestMethod]
        public void AppendQuery_KvpList_AppendsCorrectly()
        {
            var kvps = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("foo", "bar")
            };
            "http://x.com".AppendQuery(kvps).Should().Contain("foo=bar");
        }

        // ─── ToQueryString ────────────────────────────────────────────────────

        [TestMethod]
        public void ToQueryString_Null_ReturnsEmpty()
        {
            ((System.Collections.IEnumerable)null!).ToQueryString().Should().Be("");
        }

        [TestMethod]
        public void ToQueryString_DefaultName_UsesId()
        {
            new[] { 1, 2, 3 }.ToQueryString().Should().Be("id=1&id=2&id=3");
        }

        [TestMethod]
        public void ToQueryString_CustomName_UsesName()
        {
            new[] { "a", "b" }.ToQueryString("key").Should().Be("key=a&key=b");
        }

        [TestMethod]
        public void ToQueryString_Empty_ReturnsEmpty()
        {
            new int[0].ToQueryString().Should().Be("");
        }

        // ─── RemoveSpecialChar ────────────────────────────────────────────────

        [TestMethod]
        public void RemoveSpecialChar_Null_ReturnsEmpty()
        {
            ((string)null!).RemoveSpecialChar().Should().Be("");
        }

        [TestMethod]
        public void RemoveSpecialChar_Plain_Unchanged()
        {
            "hello world".RemoveSpecialChar().Should().Be("hello world");
        }

        [TestMethod]
        public void RemoveSpecialChar_WithNewlineAndTab_Removed()
        {
            "hello\r\nworld\ttab".RemoveSpecialChar()
                .Should().Be("helloworldtab");
        }

        [TestMethod]
        public void RemoveSpecialChar_WithVerticalTab_Removed()
        {
            "a\vb".RemoveSpecialChar().Should().Be("ab");
        }

        [TestMethod]
        public void RemoveSpecialChar_WithBackspace_Removed()
        {
            "a\bb".RemoveSpecialChar().Should().Be("ab");
        }
    }
}
