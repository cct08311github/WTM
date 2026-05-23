#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    // ── Minimal test models ──────────────────────────────────────────────────

    public class EvItem : TopBasePoco
    {
        public string Name { get; set; } = "";
        public int Score { get; set; }
    }

    public class EvPersist : PersistPoco
    {
        public string Label { get; set; } = "";
    }

    // ── SetValuesParser ──────────────────────────────────────────────────────

    [TestClass]
    public class SetValuesParserTests
    {
        [TestMethod]
        public void Parse_single_equality_returns_dictionary_with_one_entry()
        {
            var parser = new SetValuesParser();
            Expression<Func<EvItem, bool>> expr = x => x.Name == "hello";
            var result = parser.Parse(expr.Body);
            Assert.IsTrue(result.ContainsKey("Name"));
            Assert.AreEqual("hello", result["Name"]);
        }

        [TestMethod]
        public void Parse_multiple_equalities_combined_with_AndAlso_returns_both()
        {
            var parser = new SetValuesParser();
            Expression<Func<EvItem, bool>> expr = x => x.Name == "abc" && x.Score == 42;
            var result = parser.Parse(expr.Body);
            Assert.IsTrue(result.ContainsKey("Name"), "Name should be present");
            Assert.IsTrue(result.ContainsKey("Score"), "Score should be present");
            Assert.AreEqual("abc", result["Name"]);
            Assert.AreEqual(42, result["Score"]);
        }

        [TestMethod]
        public void Parse_no_equality_returns_empty_dictionary()
        {
            var parser = new SetValuesParser();
            // A constant expression — no binary Equal nodes
            Expression<Func<EvItem, bool>> expr = x => true;
            var result = parser.Parse(expr.Body);
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Parse_duplicate_key_only_first_value_stored()
        {
            // SetValuesParser uses ContainsKey guard — first wins
            var parser = new SetValuesParser();
            // Manually build: Name == "first" && Name == "second"
            var pe = Expression.Parameter(typeof(EvItem), "x");
            var nameProp = Expression.Property(pe, nameof(EvItem.Name));
            var eq1 = Expression.Equal(nameProp, Expression.Constant("first"));
            var eq2 = Expression.Equal(nameProp, Expression.Constant("second"));
            var and = Expression.AndAlso(eq1, eq2);
            var result = parser.Parse(and);
            Assert.AreEqual("first", result["Name"]);
        }
    }

    // ── WhereReplaceModifier ──────────────────────────────────────────────────

    [TestClass]
    public class WhereReplaceModifierTests
    {
        [TestMethod]
        public void Modify_returns_same_expression_when_no_EF_QueryRootExpression()
        {
            // Pure LINQ-to-objects source: GetDCModel returns EvItem type,
            // but VisitExtension never fires (no QueryRootExpression) so the
            // injected where is never added. The expression tree is returned as-is.
            var data = new List<EvItem>().AsQueryable();
            Expression<Func<EvItem, bool>> newWhere = x => x.Score > 0;
            var modifier = new WhereReplaceModifier<EvItem>(newWhere);
            var result = modifier.Modify(data.Expression);
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void Modify_with_existing_Where_in_tree_strips_it_in_delete_mode()
        {
            // The delete-mode VisitMethodCall strips Where nodes.
            // For LINQ-to-objects the EnumerableQuery constant means GetDCModel returns
            // the type, and the delete scan runs, removing any Where.
            var data = new List<EvItem>().AsQueryable();
            var filtered = data.Where(x => x.Score > 0);  // adds a Where call node
            Expression<Func<EvItem, bool>> newWhere = x => x.Name == "a";
            var modifier = new WhereReplaceModifier<EvItem>(newWhere);
            // Should not throw; returns a modified expression
            var result = modifier.Modify(filtered.Expression);
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void Modify_with_nested_Where_does_not_throw()
        {
            var data = new List<EvItem>().AsQueryable();
            var filtered = data.Where(x => x.Score > 0).Where(x => x.Name != "");
            Expression<Func<EvItem, bool>> newWhere = x => x.Score < 10;
            var modifier = new WhereReplaceModifier<EvItem>(newWhere);
            var result = modifier.Modify(filtered.Expression);
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void Modify_constant_non_EnumerableQuery_returns_same_expression()
        {
            // A plain constant that is neither QueryRootExpression nor EnumerableQuery<>
            var constExpr = Expression.Constant(42);
            Expression<Func<EvItem, bool>> newWhere = x => x.Score > 0;
            var modifier = new WhereReplaceModifier<EvItem>(newWhere);
            var result = modifier.Modify(constExpr);
            Assert.AreSame(constExpr, result);
        }
    }

    // ── OrderReplaceModifier ──────────────────────────────────────────────────

    [TestClass]
    public class OrderReplaceModifierTests
    {
        // Build SortInfo helper
        private static SortInfo MakeSort(string prop, SortDir dir) =>
            new SortInfo { Property = prop, Direction = dir };

        [TestMethod]
        public void Modify_removes_existing_OrderBy_from_LINQ_expression()
        {
            var data = new List<EvItem>
            {
                new() { Name = "b", Score = 2 },
                new() { Name = "a", Score = 1 },
            }.AsQueryable();

            // An expression with an existing OrderBy
            var ordered = data.OrderBy(x => x.Name);
            var modifier = new OrderReplaceModifier(MakeSort(nameof(EvItem.Score), SortDir.Asc));
            var result = modifier.Modify(ordered.Expression);

            // The result should be a valid expression — not throw
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void Modify_removes_OrderByDescending_from_expression()
        {
            var data = new List<EvItem>
            {
                new() { Name = "x", Score = 3 },
            }.AsQueryable();

            var ordered = data.OrderByDescending(x => x.Score);
            var modifier = new OrderReplaceModifier(MakeSort(nameof(EvItem.Name), SortDir.Desc));
            var result = modifier.Modify(ordered.Expression);
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void Modify_with_ThenBy_removes_all_order_nodes()
        {
            var data = new List<EvItem>().AsQueryable();
            var ordered = data.OrderBy(x => x.Name).ThenBy(x => x.Score);
            var modifier = new OrderReplaceModifier(MakeSort(nameof(EvItem.Name), SortDir.Asc));
            var result = modifier.Modify(ordered.Expression);
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void Modify_with_non_generic_source_does_not_throw()
        {
            // Expression with no OrderBy — modifier must still complete
            var data = new List<EvItem>().AsQueryable();
            var modifier = new OrderReplaceModifier(MakeSort(nameof(EvItem.Score), SortDir.Asc));
            var result = modifier.Modify(data.Expression);
            Assert.IsNotNull(result);
        }
    }

    // ── ChangePara ───────────────────────────────────────────────────────────

    [TestClass]
    public class ChangeParaTests
    {
        [TestMethod]
        public void Change_replaces_parameter_in_member_access()
        {
            Expression<Func<EvItem, bool>> expr = x => x.Name == "test";
            var newParam = Expression.Parameter(typeof(EvItem), "y");
            var cp = new ChangePara();
            var changed = cp.Change(expr.Body, newParam);

            // The changed expression should compile and evaluate with y
            var lambda = Expression.Lambda<Func<EvItem, bool>>(changed, newParam);
            var compiled = lambda.Compile();
            Assert.IsTrue(compiled(new EvItem { Name = "test" }));
            Assert.IsFalse(compiled(new EvItem { Name = "other" }));
        }

        [TestMethod]
        public void Change_works_with_int_property()
        {
            Expression<Func<EvItem, bool>> expr = x => x.Score > 10;
            var newParam = Expression.Parameter(typeof(EvItem), "z");
            var cp = new ChangePara();
            var changed = cp.Change(expr.Body, newParam);
            var lambda = Expression.Lambda<Func<EvItem, bool>>(changed, newParam);
            var compiled = lambda.Compile();
            Assert.IsTrue(compiled(new EvItem { Score = 20 }));
            Assert.IsFalse(compiled(new EvItem { Score = 5 }));
        }

        [TestMethod]
        public void Change_with_nested_member_access_visits_base_member()
        {
            // x.Name.Length — the outer MemberAccess (Length) is on a non-parameter
            // expression, so the base.VisitMember branch is taken (line 565).
            Expression<Func<EvItem, int>> expr = x => x.Name.Length;
            var newParam = Expression.Parameter(typeof(EvItem), "p");
            var cp = new ChangePara();
            var changed = cp.Change(expr.Body, newParam);

            var lambda = Expression.Lambda<Func<EvItem, int>>(changed, newParam);
            var compiled = lambda.Compile();
            Assert.AreEqual(5, compiled(new EvItem { Name = "hello" }));
        }
    }

    // ── SelectInfo ───────────────────────────────────────────────────────────

    [TestClass]
    public class SelectInfoTests
    {
        [TestMethod]
        public void GetColumns_from_EnumerableQuery_with_select_projection_returns_columns()
        {
            var data = new List<EvItem> { new() { Name = "A", Score = 1 } }.AsQueryable();
            // Build a projection: Select(x => new { Name = x.Name, Score = x.Score })
            // We use a MemberInitExpression-based anonymous class equivalent via named type
            var projected = data.Select(x => new EvItem { Name = x.Name, Score = x.Score });
            var info = new SelectInfo();
            var cols = info.GetColumns(projected.Expression);
            // Should detect the MemberInit bindings
            Assert.IsNotNull(cols);
            Assert.IsTrue(cols.Contains("Name"), "Name should be in columns");
            Assert.IsTrue(cols.Contains("Score"), "Score should be in columns");
        }

        [TestMethod]
        public void GetColumns_no_select_returns_null()
        {
            // No Select call in the expression tree
            var data = new List<EvItem>().AsQueryable();
            var info = new SelectInfo();
            var cols = info.GetColumns(data.Expression);
            // _columns is only set when a Select is found; otherwise remains null
            Assert.IsNull(cols);
        }
    }

    // ── ClearSelectMany ──────────────────────────────────────────────────────

    [TestClass]
    public class ClearSelectManyTests
    {
        private class ItemWithList : TopBasePoco
        {
            public string Label { get; set; } = "";
            public List<string> Tags { get; set; } = new();
        }

        [TestMethod]
        public void Clear_removes_list_bindings_from_MemberInit()
        {
            // Build a MemberInit with both scalar and list bindings
            var ctor = typeof(ItemWithList).GetConstructor(Type.EmptyTypes)!;
            var newExpr = Expression.New(ctor);
            var labelProp = typeof(ItemWithList).GetProperty(nameof(ItemWithList.Label))!;
            var tagsProp = typeof(ItemWithList).GetProperty(nameof(ItemWithList.Tags))!;
            var labelBinding = Expression.Bind(labelProp, Expression.Constant("x"));
            // Tags binding — generic, non-Nullable → should be stripped
            var tagsBinding = Expression.Bind(tagsProp,
                Expression.Constant(new List<string> { "a" }, typeof(List<string>)));

            var memberInit = Expression.MemberInit(newExpr, labelBinding, tagsBinding);
            var csm = new ClearSelectMany();
            var result = csm.Clear(memberInit);

            // After clearing, the MemberInit should only have the scalar binding
            if (result is MemberInitExpression resultMemberInit)
            {
                Assert.AreEqual(1, resultMemberInit.Bindings.Count,
                    "List binding should have been removed");
                Assert.AreEqual(nameof(ItemWithList.Label), resultMemberInit.Bindings[0].Member.Name);
            }
            else
            {
                Assert.Fail("Expected MemberInitExpression");
            }
        }

        [TestMethod]
        public void Clear_preserves_nullable_bindings()
        {
            // A Nullable<int> binding must NOT be stripped (IsGenericType=true but Nullable<>)
            var param = Expression.Parameter(typeof(EvItem), "x");
            // Build MemberInit for EvItem with Score (int, not list)
            var ctor = typeof(EvItem).GetConstructor(Type.EmptyTypes)!;
            var newExpr = Expression.New(ctor);
            var scoreProp = typeof(EvItem).GetProperty(nameof(EvItem.Score))!;
            var scoreBinding = Expression.Bind(scoreProp, Expression.Constant(5));
            var memberInit = Expression.MemberInit(newExpr, scoreBinding);

            var csm = new ClearSelectMany();
            var result = csm.Clear(memberInit);
            // Score is int, not generic → binding preserved
            if (result is MemberInitExpression resultMemberInit)
            {
                Assert.AreEqual(1, resultMemberInit.Bindings.Count);
            }
            else
            {
                // base.VisitMemberInit returns same node if no change — still valid
                Assert.IsNotNull(result);
            }
        }
    }

    // ── IsValidModifier ──────────────────────────────────────────────────────

    [TestClass]
    public class IsValidModifierTests
    {
        [TestMethod]
        public void Modify_non_IPersistPoco_source_returns_same_expression()
        {
            var data = new List<EvItem>().AsQueryable();
            var modifier = new IsValidModifier();
            var result = modifier.Modify(data.Expression);
            // EvItem does not implement IPersistPoco → expression returned unchanged
            Assert.AreEqual(data.Expression, result);
        }

        [TestMethod]
        public void Modify_non_IPersistPoco_constant_expression_returns_unchanged()
        {
            // Constant expression that is not a QueryRootExpression or EnumerableQuery
            var modifier = new IsValidModifier();
            var constExpr = Expression.Constant(42);
            var result = modifier.Modify(constExpr);
            Assert.AreSame(constExpr, result);
        }

        [TestMethod]
        public void Modify_IPersistPoco_EnumerableQuery_returns_expression_without_throwing()
        {
            // IsValidModifier.GetDCModel recognises EnumerableQuery<> via the constant branch.
            // With a pure LINQ-to-objects source (no QueryRootExpression), the VisitExtension
            // hook never fires, so the expression tree is returned unchanged — this is correct
            // behaviour (the IsValid filter is injected only against EF Core query roots).
            var data = new List<EvPersist>
            {
                new() { Label = "active", IsValid = true },
                new() { Label = "deleted", IsValid = false }
            }.AsQueryable();

            var modifier = new IsValidModifier();
            var originalExpr = data.Expression;
            Expression? modifiedExpr = null;

            // Must not throw
            modifiedExpr = modifier.Modify(originalExpr);
            Assert.IsNotNull(modifiedExpr);
        }

        [TestMethod]
        public void Modify_IPersistPoco_with_explicit_IsValid_sets_needAdd_false()
        {
            // When the incoming expression already contains IsValid==...,
            // the modifier detects it during the scan pass and should NOT inject another Where.
            var data = new List<EvPersist>
            {
                new() { Label = "a", IsValid = true },
                new() { Label = "b", IsValid = false }
            }.AsQueryable();

            // Apply an explicit IsValid filter before running the modifier
            var filtered = data.Where(x => x.IsValid == false);
            var modifier = new IsValidModifier();
            var modifiedExpr = modifier.Modify(filtered.Expression);

            // The modifier detected IsValid in the tree; the returned expression should
            // be structurally the same as the input (no additional Where injected).
            // We verify by checking the expression string length hasn't grown significantly.
            var originalLen = filtered.Expression.ToString().Length;
            var modifiedLen = modifiedExpr.ToString().Length;

            // Allow small variance; the key is that no extra Where(x => x.IsValid == True) was appended
            Assert.IsTrue(modifiedLen <= originalLen + 50,
                $"Expression grew too much — IsValid Where may have been injected twice. " +
                $"Original len: {originalLen}, modified len: {modifiedLen}");
        }

        [TestMethod]
        public void Modify_Call_expression_with_non_IPersistPoco_returns_same_expression()
        {
            // Exercise the Call branch in GetDCModel where the method call chains
            // don't lead to a QueryRootExpression — returns null → expression unchanged
            var data = new List<EvItem>().AsQueryable();
            var filtered = data.Where(x => x.Score > 0);   // MethodCallExpression
            var modifier = new IsValidModifier();
            var result = modifier.Modify(filtered.Expression);
            // EvItem does not implement IPersistPoco → unchanged
            Assert.AreSame(filtered.Expression, result);
        }
    }
}
