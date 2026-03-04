using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using Xunit;

namespace WalkingTec.Mvvm.Core.Tests.Integration
{
    /// <summary>
    /// Tests for async ViewModel methods added in v8.1.13/v8.1.14.
    /// Mirrors sync tests in test/WalkingTec.Mvvm.Core.Test/VM/BaseCRUDVMTest.cs
    /// but uses async variants (DoAddAsync / DoEditAsync / DoDeleteAsync).
    ///
    /// Uses a minimal TestNote entity to avoid complex relationship setup.
    /// Elsa workflow is NOT triggered because TestNote does not implement IWorkflow.
    /// </summary>
    public class BaseCRUDVMAsyncTests
    {
        private readonly string _seed;

        public BaseCRUDVMAsyncTests()
        {
            _seed = Guid.NewGuid().ToString();
        }

        private IDataContext CreateDb() =>
            new AsyncVMTestDataContext(_seed, DBTypeEnum.Memory);

        // ─── DoAddAsync ────────────────────────────────────────────────────────

        [Fact]
        public async Task DoAddAsync_ValidEntity_PersistsToDbWithAuditFields()
        {
            var vm = new BaseCRUDVM<TestNote>
            {
                Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "async_user")
            };
            vm.Entity = new TestNote { Title = "async_add_test", Content = "content1" };

            await vm.DoAddAsync();

            using var db = CreateDb();
            var saved = ((DbContext)db).Set<TestNote>().FirstOrDefault();
            saved.Should().NotBeNull();
            saved!.Title.Should().Be("async_add_test");
            saved.CreateBy.Should().Be("async_user");
            saved.CreateTime.Should().NotBeNull();
            saved.CreateTime!.Value.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(10));
            vm.MSD.Count.Should().Be(0);
        }

        [Fact]
        public async Task DoAddAsync_TwoEntities_BothPersist()
        {
            var vm1 = new BaseCRUDVM<TestNote>
            {
                Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user1")
            };
            var vm2 = new BaseCRUDVM<TestNote>
            {
                Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user2")
            };

            vm1.Entity = new TestNote { Title = "first", Content = "a" };
            vm2.Entity = new TestNote { Title = "second", Content = "b" };

            await vm1.DoAddAsync();
            await vm2.DoAddAsync();

            using var db = CreateDb();
            ((DbContext)db).Set<TestNote>().Count().Should().Be(2);
        }

        // ─── DoEditAsync ───────────────────────────────────────────────────────

        [Fact]
        public async Task DoEditAsync_ExistingEntity_UpdatesFieldsAndAuditInfo()
        {
            // Arrange: add entity via sync first
            TestNote original;
            using (var db = CreateDb())
            {
                original = new TestNote { Title = "original", Content = "old_content" };
                ((DbContext)db).Set<TestNote>().Add(original);
                await ((DbContext)db).SaveChangesAsync();
            }

            // Act: edit via async
            var vm = new BaseCRUDVM<TestNote>
            {
                Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "editor_user")
            };
            using (var db = CreateDb())
            {
                vm.DC = db;
                vm.Entity = new TestNote
                {
                    ID = original.ID,
                    Title = "updated",
                    Content = "new_content"
                };
                await vm.DoEditAsync(updateAllFields: true);
            }

            // Assert
            using var assertDb = CreateDb();
            var updated = await ((DbContext)assertDb).Set<TestNote>().FindAsync(original.ID);
            updated.Should().NotBeNull();
            updated!.Title.Should().Be("updated");
            updated.Content.Should().Be("new_content");
            updated.UpdateBy.Should().Be("editor_user");
            updated.UpdateTime.Should().NotBeNull();
            updated.UpdateTime!.Value.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task DoEditAsync_FieldSubset_UpdatesOnlySpecifiedFields()
        {
            // Arrange
            TestNote original;
            using (var db = CreateDb())
            {
                original = new TestNote { Title = "keep_title", Content = "keep_content" };
                ((DbContext)db).Set<TestNote>().Add(original);
                await ((DbContext)db).SaveChangesAsync();
            }

            // Act: edit only Title, not Content
            var vm = new BaseCRUDVM<TestNote>
            {
                Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "partial_editor")
            };
            using (var db = CreateDb())
            {
                vm.DC = db;
                vm.Entity = new TestNote
                {
                    ID = original.ID,
                    Title = "changed_title",
                    Content = "should_be_ignored"
                };
                vm.FC = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["Entity.Title"] = ""
                };
                await vm.DoEditAsync(updateAllFields: false);
            }

            // Assert: only Title changed
            using var assertDb = CreateDb();
            var result = await ((DbContext)assertDb).Set<TestNote>().FindAsync(original.ID);
            result!.Title.Should().Be("changed_title");
            result.Content.Should().Be("keep_content", "Content not in FC, must not change");
        }

        // ─── DoDeleteAsync ─────────────────────────────────────────────────────

        [Fact]
        public async Task DoDeleteAsync_ExistingEntity_RemovesFromDb()
        {
            // Arrange
            TestNote entity;
            using (var db = CreateDb())
            {
                entity = new TestNote { Title = "to_delete", Content = "bye" };
                ((DbContext)db).Set<TestNote>().Add(entity);
                await ((DbContext)db).SaveChangesAsync();
            }

            // Act
            var vm = new BaseCRUDVM<TestNote>
            {
                Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "deleter")
            };
            using (var db = CreateDb())
            {
                vm.DC = db;
                vm.Entity = new TestNote { ID = entity.ID };
                await vm.DoDeleteAsync();
            }

            // Assert
            using var assertDb = CreateDb();
            ((DbContext)assertDb).Set<TestNote>().Count().Should().Be(0);
        }

        [Fact]
        public async Task DoDeleteAsync_MultipleEntities_DeletesOnlyTarget()
        {
            // Arrange: two entities
            TestNote keep, remove;
            using (var db = CreateDb())
            {
                keep = new TestNote { Title = "keep", Content = "here" };
                remove = new TestNote { Title = "remove", Content = "gone" };
                ((DbContext)db).Set<TestNote>().AddRange(keep, remove);
                await ((DbContext)db).SaveChangesAsync();
            }

            // Act
            var vm = new BaseCRUDVM<TestNote>
            {
                Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "deleter")
            };
            using (var db = CreateDb())
            {
                vm.DC = db;
                vm.Entity = new TestNote { ID = remove.ID };
                await vm.DoDeleteAsync();
            }

            // Assert
            using var assertDb = CreateDb();
            var remaining = ((DbContext)assertDb).Set<TestNote>().ToList();
            remaining.Should().HaveCount(1);
            remaining[0].Title.Should().Be("keep");
        }
    }

    /// <summary>Minimal test entity with audit fields.</summary>
    public class TestNote : BasePoco
    {
        public string Title { get; set; } = null!;
        public string Content { get; set; } = null!;
    }

    internal class AsyncVMTestDataContext : EmptyContext
    {
        public AsyncVMTestDataContext(string cs, DBTypeEnum dbtype)
            : base(cs, dbtype) { }

        public DbSet<TestNote> TestNotes { get; set; } = null!;
    }
}
