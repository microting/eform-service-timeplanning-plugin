/*
The MIT License (MIT)

Copyright (c) 2007 - 2026 Microting A/S

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System.Collections.Generic;
using System.Linq;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using NUnit.Framework;
using ServiceTimePlanningPlugin.Infrastructure.Helpers;

namespace ServiceTimePlanningPlugin.Integration.Test;

/// <summary>
/// Covers the PlanTimer sheet header mapping and site resolution SearchListJob
/// imports through. Every header row starts with the three non-worker columns
/// the job skips, so the first worker column is index 3 (column D).
/// </summary>
[TestFixture]
public class PlanTimerSheetColumnsTests
{
    private static List<object> Headers(params string[] workerHeaders) =>
        new object[] { "Dato", "Ugedag", "Uge" }.Concat(workerHeaders).ToList();

    private static void AssertColumns(PlanTimerSheetColumns.Layout layout, string key, int? hours, int? text)
    {
        var worker = layout.Workers.Single(x => x.Key == key);
        Assert.That((worker.HoursColumn, worker.TextColumn), Is.EqualTo((hours, text)), key);
    }

    private static PlanTimerSheetColumns.WorkerColumns Columns(string name, int? hours = 3, int? text = 4) =>
        new(PlanTimerSheetColumns.NormalizeName(name), name, hours, text);

    private static AssignedSite Assigned(int siteId, bool importing = true, bool resigned = false) =>
        new() { SiteId = siteId, UseGoogleSheetAsDefault = importing, Resigned = resigned };

    [Test]
    public void Map_CanonicalPairs_MapsHoursThenText()
    {
        var layout = PlanTimerSheetColumns.Map(Headers(
            "Albert Doba - timer", "Albert Doba - tekst",
            "Phien Van Le - timer", "Phien Van Le - tekst"));

        Assert.That(layout.Problems, Is.Empty);
        Assert.That(layout.Workers, Is.EqualTo(new[]
        {
            new PlanTimerSheetColumns.WorkerColumns("albertdoba", "Albert Doba", 3, 4),
            new PlanTimerSheetColumns.WorkerColumns("phienvanle", "Phien Van Le", 5, 6)
        }));
    }

    /// <summary>
    /// Tenant 1063: a text header landed where the fixed stride expected an
    /// hours header, so Phien's column read as "phienvanle-tekst" and matched
    /// no site. Mapping by name must find both columns regardless of order.
    /// </summary>
    [Test]
    public void Map_TextBeforeHoursAfterAStrayColumn_StillPairsEachWorkerByName()
    {
        var layout = PlanTimerSheetColumns.Map(Headers(
            "Malaika Luna Jørgensen - timer",
            "Valentin - - tekst",
            "Valentin - - timer",
            "Phien Van Le - tekst",
            "Phien Van Le - timer",
            "Serhii Vashchuk - timer", "Serhii Vashchuk - tekst"));

        Assert.That(layout.Problems, Is.Empty);
        AssertColumns(layout, "malaikalunajørgensen", 3, null);
        AssertColumns(layout, "valentin", 5, 4);
        AssertColumns(layout, "phienvanle", 7, 6);
        AssertColumns(layout, "serhiivashchuk", 8, 9);
    }

    [Test]
    public void Map_SuffixCaseSpacingAndDashes_AreIgnored()
    {
        var layout = PlanTimerSheetColumns.Map(Headers(
            "Phien Van Le -Timer", "Phien Van Le  -  TEKST ",
            "Ngoan Tran – timer", "Ngoan Tran—tekst"));

        Assert.That(layout.Problems, Is.Empty);
        AssertColumns(layout, "phienvanle", 3, 4);
        AssertColumns(layout, "ngoantran", 5, 6);
    }

    [Test]
    public void Map_LegacyHeaderWithoutSuffix_ReadsTheNextColumnAsText()
    {
        var layout = PlanTimerSheetColumns.Map(Headers(
            "Albert Doba", "", "Svend Gammelgård", "Tekst", "Ngoan Tran - timer", "Ngoan Tran - tekst"));

        Assert.That(layout.Problems, Is.Empty);
        Assert.That(layout.Workers.Select(x => x.Key), Is.EqualTo(new[] { "albertdoba", "svendgammelgård", "ngoantran" }));
        AssertColumns(layout, "albertdoba", 3, 4);
        AssertColumns(layout, "svendgammelgård", 5, 6);
        AssertColumns(layout, "ngoantran", 7, 8);
    }

    [Test]
    public void Map_BareColumnLabels_AreNeitherWorkersNorClaimTheNextColumn()
    {
        var layout = PlanTimerSheetColumns.Map(Headers("", "Tekst", "Albert Doba", "Tekst", "Timer"));

        Assert.That(layout.Problems, Is.Empty);
        Assert.That(layout.Workers.Select(x => x.Key), Is.EqualTo(new[] { "albertdoba" }));
        AssertColumns(layout, "albertdoba", 5, 6);
    }

    [Test]
    public void Map_LegacyHeaderLastInRow_UsesTheColumnTheApiTrimmedForText()
    {
        AssertColumns(PlanTimerSheetColumns.Map(Headers("Albert Doba")), "albertdoba", 3, 4);
    }

    [Test]
    public void Map_LegacyHeaderBeforeAnotherWorkersSuffixedHeader_HasNoText()
    {
        var layout = PlanTimerSheetColumns.Map(Headers("Albert Doba", "Ngoan Tran - timer", "Ngoan Tran - tekst"));

        AssertColumns(layout, "albertdoba", 3, null);
        AssertColumns(layout, "ngoantran", 4, 5);
    }

    [Test]
    public void Map_LegacyHeaderWithSuffixedText_PairsByName()
    {
        var layout = PlanTimerSheetColumns.Map(Headers("Albert Doba", "Albert Doba - tekst"));

        Assert.That(layout.Problems, Is.Empty);
        AssertColumns(layout, "albertdoba", 3, 4);
    }

    [Test]
    public void Map_TextOnlyWorker_IsKept()
    {
        var layout = PlanTimerSheetColumns.Map(Headers("Phien Van Le - tekst"));

        Assert.That(layout.Problems, Is.Empty);
        AssertColumns(layout, "phienvanle", null, 3);
    }

    [Test]
    public void Map_DuplicateHeader_KeepsTheFirstAndNamesBothColumns()
    {
        var layout = PlanTimerSheetColumns.Map(Headers(
            "Phien Van Le - timer", "Phien Van Le - tekst", "Phien Van Le - timer"));

        AssertColumns(layout, "phienvanle", 3, 4);
        Assert.That(layout.Problems, Is.EqualTo(new[]
        {
            "column F header \"Phien Van Le - timer\" duplicates column D, which is used instead"
        }));
    }

    [Test]
    public void Map_LegacyHeaderDuplicatingASuffixedOne_KeepsTheSuffixedColumn()
    {
        var layout = PlanTimerSheetColumns.Map(Headers("Albert Doba", "", "Albert Doba - timer", "Albert Doba - tekst"));

        AssertColumns(layout, "albertdoba", 5, 6);
        Assert.That(layout.Problems.Single(), Does.StartWith("column D header \"Albert Doba\" duplicates column F"));
    }

    [Test]
    public void Map_BlankAndNamelessHeaders_AreNotWorkers()
    {
        var layout = PlanTimerSheetColumns.Map(Headers("", "   ", " - timer", "-"));

        Assert.That(layout.Workers, Is.Empty);
        Assert.That(layout.Problems, Has.Count.EqualTo(2));
    }

    [Test]
    public void Resolve_MatchesSiteNamesIgnoringCaseSpacesAndDashes()
    {
        var resolution = PlanTimerSheetColumns.Resolve(
            [Columns("Phien Van Le"), Columns("Julius -")],
            [("phien  van LE", 11), ("Julius", 12)],
            [Assigned(11), Assigned(12)]);

        Assert.That(resolution.Problems, Is.Empty);
        Assert.That(resolution.Unmatched, Is.Empty);
        Assert.That(resolution.Workers.Select(x => (x.Columns.Key, x.SiteId)),
            Is.EqualTo(new[] { ("phienvanle", 11), ("julius", 12) }));
    }

    [Test]
    public void Resolve_SiteNotAssignedOrNotImporting_IsUnmatchedNotAProblem()
    {
        var resolution = PlanTimerSheetColumns.Resolve(
            [Columns("Phien Van Le"), Columns("Ngoan Tran"), Columns("Total")],
            [("Phien Van Le", 11), ("Ngoan Tran", 12)],
            [Assigned(11, importing: false)]);

        Assert.That(resolution.Workers, Is.Empty);
        Assert.That(resolution.Problems, Is.Empty);
        Assert.That(resolution.Unmatched, Has.Count.EqualTo(3));
    }

    [Test]
    public void Resolve_ResignedNamesake_YieldsToTheActiveWorker()
    {
        var resolution = PlanTimerSheetColumns.Resolve(
            [Columns("Phien Van Le")],
            [("Phien Van Le", 11), ("Phien Van Le", 12)],
            [Assigned(11, resigned: true), Assigned(12)]);

        Assert.That(resolution.Problems, Is.Empty);
        Assert.That(resolution.Workers.Single().SiteId, Is.EqualTo(12));
    }

    [Test]
    public void Resolve_NamesakeThatDoesNotImport_DoesNotMakeTheColumnAmbiguous()
    {
        var resolution = PlanTimerSheetColumns.Resolve(
            [Columns("Phien Van Le")],
            [("Phien Van Le", 11), ("Phien-Van Le", 12)],
            [Assigned(11), Assigned(12, importing: false)]);

        Assert.That(resolution.Problems, Is.Empty);
        Assert.That(resolution.Workers.Single().SiteId, Is.EqualTo(11));
    }

    [Test]
    public void Resolve_TwoActiveImportingNamesakes_ImportsNeitherAndReports()
    {
        var resolution = PlanTimerSheetColumns.Resolve(
            [Columns("Phien Van Le")],
            [("Phien Van Le", 11), ("Phien-Van Le", 12)],
            [Assigned(11), Assigned(12)]);

        Assert.That(resolution.Workers, Is.Empty);
        Assert.That(resolution.Problems.Single(), Does.Contain("matches 2 sites"));
    }

    [Test]
    public void Resolve_ActiveImportingSiteWithoutAColumn_IsReportedAsUnmatched()
    {
        var resolution = PlanTimerSheetColumns.Resolve(
            [Columns("Phien Van Le - tekst")],
            [("Phien Van Le", 11), ("Ngoan Tran", 12), ("Albert Doba", 13)],
            [Assigned(11), Assigned(12, resigned: true), Assigned(13, importing: false)]);

        Assert.That(resolution.Workers, Is.Empty);
        Assert.That(resolution.Unmatched, Is.EquivalentTo(new[]
        {
            "site \"Phien Van Le\" imports from the sheet but no column names it",
            "column \"Phien Van Le - tekst\" matches no site importing from the sheet"
        }));
    }

    [Test]
    public void Resolve_MissingHalfOfAPair_IsImportedAndReported()
    {
        var resolution = PlanTimerSheetColumns.Resolve(
            [Columns("Phien Van Le", hours: null, text: 3), Columns("Ngoan Tran", hours: 4, text: null)],
            [("Phien Van Le", 11), ("Ngoan Tran", 12)],
            [Assigned(11), Assigned(12)]);

        Assert.That(resolution.Workers, Has.Count.EqualTo(2));
        Assert.That(resolution.Problems, Has.Count.EqualTo(2));
        Assert.That(resolution.Problems[0], Does.Contain("Phien Van Le").And.Contain("hours are left unchanged"));
        Assert.That(resolution.Problems[1], Does.Contain("Ngoan Tran").And.Contain("text is left unchanged"));
    }

    [TestCase("Phien Van Le", "phienvanle")]
    [TestCase("  phien  VAN le ", "phienvanle")]
    [TestCase("Julius -", "julius")]
    [TestCase("Phien Van\tLe", "phienvanle")]
    [TestCase("Anne–Marie", "annemarie")]
    [TestCase(null, "")]
    public void NormalizeName_StripsWhitespaceAndDashesAndLowerCases(string name, string expected)
    {
        Assert.That(PlanTimerSheetColumns.NormalizeName(name), Is.EqualTo(expected));
    }

    [TestCase(0, "A")]
    [TestCase(3, "D")]
    [TestCase(25, "Z")]
    [TestCase(26, "AA")]
    [TestCase(701, "ZZ")]
    [TestCase(702, "AAA")]
    public void ColumnLetter_MatchesTheSheetsColumnNames(int col, string expected)
    {
        Assert.That(PlanTimerSheetColumns.ColumnLetter(col), Is.EqualTo(expected));
    }

    [TestCase("", 0.0)]
    [TestCase("   ", 0.0)]
    [TestCase("7,5", 7.5)]
    [TestCase(" 7.5 ", 7.5)]
    [TestCase("8", 8.0)]
    [TestCase("abc", null)]
    [TestCase("-1", null)]
    [TestCase("NaN", null)]
    [TestCase("Infinity", null)]
    public void ParseHours_BlankIsZeroAndAnythingButAFiniteNumberIsNull(string cell, double? expected)
    {
        Assert.That(PlanTimerSheetColumns.ParseHours(cell), Is.EqualTo(expected));
    }

    [Test]
    public void ApplyTo_BothValues_SetsTextAndLeavesHoursWhenTextIsPlanned()
    {
        var planRegistration = new PlanRegistration { PlanText = "old", PlanHours = 4 };

        PlanTimerSheetColumns.ApplyTo(planRegistration, "7-15", 8, "test");

        Assert.That((planRegistration.PlanText, planRegistration.PlanHours), Is.EqualTo(("7-15", 4.0)));
    }

    [Test]
    public void ApplyTo_EmptyText_SetsHours()
    {
        var planRegistration = new PlanRegistration { PlanText = "old", PlanHours = 4 };

        PlanTimerSheetColumns.ApplyTo(planRegistration, "", 8, "test");

        Assert.That((planRegistration.PlanText, planRegistration.PlanHours), Is.EqualTo(("", 8.0)));
    }

    [Test]
    public void ApplyTo_HoursChangedByAdmin_AreKept()
    {
        var planRegistration = new PlanRegistration { PlanText = "", PlanHours = 4, PlanChangedByAdmin = true };

        PlanTimerSheetColumns.ApplyTo(planRegistration, "", 8, "test");

        Assert.That(planRegistration.PlanHours, Is.EqualTo(4.0));
    }

    /// <summary>
    /// A column the sheet lacks, or an hours cell that is not a number, is
    /// null and must never blank the text or zero the hours already stored.
    /// </summary>
    [Test]
    public void ApplyTo_NullValues_LeaveBothFieldsUntouched()
    {
        var withText = new PlanRegistration { PlanText = "7-15", PlanHours = 8 };
        var withoutText = new PlanRegistration { PlanText = "", PlanHours = 8 };

        PlanTimerSheetColumns.ApplyTo(withText, null, 0, "test");
        PlanTimerSheetColumns.ApplyTo(withoutText, "", null, "test");

        Assert.That((withText.PlanText, withText.PlanHours), Is.EqualTo(("7-15", 8.0)));
        Assert.That((withoutText.PlanText, withoutText.PlanHours), Is.EqualTo(("", 8.0)));
    }

    [Test]
    public void CellAt_ColumnBeyondTheRowOrNull_IsEmpty()
    {
        var row = new List<object> { "15.09.2026", "7,5" };

        Assert.That(PlanTimerSheetColumns.CellAt(row, 1), Is.EqualTo("7,5"));
        Assert.That(PlanTimerSheetColumns.CellAt(row, 5), Is.Empty);
        Assert.That(PlanTimerSheetColumns.CellAt(row, null), Is.Empty);
        Assert.That(PlanTimerSheetColumns.CellAt(new List<object>(), 0), Is.Empty);
    }
}
