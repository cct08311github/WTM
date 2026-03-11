using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    [TestClass]
    public class DashboardDefinitionTests
    {
        [TestMethod]
        public void Serialize_and_deserialize_round_trip()
        {
            var def = new DashboardDefinition
            {
                Id = "dash1",
                Title = "Test Dashboard",
                Owner = "admin",
                Sharing = new SharingDefinition { Mode = "public" },
                Layout = new List<LayoutItem>
                {
                    new LayoutItem { Id = "widget1", X = 0, Y = 0, W = 2, H = 2 }
                },
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    {
                        "widget1",
                        new WidgetDefinition
                        {
                            Type = "chart",
                            Title = "My Chart",
                            Source = new WidgetSourceDefinition
                            {
                                Kind = "custom"
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(def);
            var deserialized = JsonSerializer.Deserialize<DashboardDefinition>(json);

            deserialized.Should().NotBeNull();
            deserialized!.Id.Should().Be("dash1");
            deserialized.Title.Should().Be("Test Dashboard");
            deserialized.Layout.Should().HaveCount(1);
            deserialized.Layout[0].Id.Should().Be("widget1");
            deserialized.Widgets.Should().ContainKey("widget1");
        }

        [TestMethod]
        public void Deserialize_minimal_json()
        {
            var json = "{}";
            var deserialized = JsonSerializer.Deserialize<DashboardDefinition>(json);

            deserialized.Should().NotBeNull();
            deserialized!.SchemaVersion.Should().Be(1);
            deserialized.Id.Should().Be("");
            deserialized.RefreshInterval.Should().Be(60);
            deserialized.Layout.Should().NotBeNull();
            deserialized.Widgets.Should().NotBeNull();
            deserialized.Sharing.Should().NotBeNull();
            deserialized.Sharing.Mode.Should().Be("private");
        }

        [TestMethod]
        public void DashboardSummary_maps_from_definition()
        {
            var def = new DashboardDefinition
            {
                Id = "dash1",
                Title = "Test Dashboard",
                Owner = "admin",
                Sharing = new SharingDefinition { Mode = "public", Roles = new List<string> { "Admin" } },
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };

            var summary = new DashboardSummary
            {
                Id = def.Id,
                Title = def.Title,
                Owner = def.Owner,
                Sharing = def.Sharing,
                UpdatedAt = def.UpdatedAt
            };

            summary.Id.Should().Be("dash1");
            summary.Title.Should().Be("Test Dashboard");
            summary.Owner.Should().Be("admin");
            summary.Sharing.Mode.Should().Be("public");
            summary.UpdatedAt.Should().Be(def.UpdatedAt);
        }
    }
}