#nullable enable
using System;
using System.Linq.Expressions;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 建構日期截斷 Expression，將 DateTime 轉為整數 key 用於 GroupBy。
    /// 使用 .Year/.Month/.Day CLR 屬性，確保 MSSQL 和 Oracle EF Core provider 都能翻譯。
    /// </summary>
    public static class DateTruncator
    {
        /// <summary>
        /// 根據 DateHierarchy 建構截斷 Expression。
        /// 輸入為 DateTime 或 DateTime? 的 Expression，輸出為 int Expression。
        /// </summary>
        public static Expression BuildTruncExpression(Expression dateExpr, DateHierarchy hierarchy)
        {
            // Handle DateTime? -> extract .Value first (caller should filter nulls)
            var actualExpr = dateExpr;
            var exprType = dateExpr.Type;
            if (exprType == typeof(DateTime?))
                actualExpr = Expression.Property(dateExpr, nameof(Nullable<DateTime>.Value));

            return hierarchy switch
            {
                DateHierarchy.Year =>
                    // x.OrderDate.Year -> e.g. 2026
                    Expression.Property(actualExpr, nameof(DateTime.Year)),

                DateHierarchy.Quarter =>
                    // x.OrderDate.Year * 10 + ((x.OrderDate.Month - 1) / 3 + 1) -> e.g. 20261
                    BuildQuarterKey(actualExpr),

                DateHierarchy.Month =>
                    // x.OrderDate.Year * 100 + x.OrderDate.Month -> e.g. 202603
                    BuildMonthKey(actualExpr),

                DateHierarchy.Day =>
                    // x.OrderDate.Year * 10000 + x.OrderDate.Month * 100 + x.OrderDate.Day -> e.g. 20260309
                    BuildDayKey(actualExpr),

                _ => throw new ArgumentException($"DateHierarchy.{hierarchy} is not supported for truncation.")
            };
        }

        /// <summary>
        /// 將整數 key 格式化為人類可讀字串（供前端或匯出使用）。
        /// </summary>
        public static string FormatKey(int key, DateHierarchy hierarchy)
        {
            return hierarchy switch
            {
                DateHierarchy.Year => key.ToString(),
                DateHierarchy.Quarter => $"{key / 10} Q{key % 10}",
                DateHierarchy.Month => $"{key / 100}-{key % 100:D2}",
                DateHierarchy.Day => $"{key / 10000}-{key / 100 % 100:D2}-{key % 100:D2}",
                _ => key.ToString()
            };
        }

        /// <summary>
        /// Reverse of <see cref="FormatKey"/>: parse a hierarchy label
        /// (e.g. "2026", "2026 Q1", "2026-03", "2026-03-15") back into a
        /// half-open date range <c>[start, endExclusive)</c>. Returns
        /// <c>false</c> when the label doesn't match the expected
        /// hierarchy shape — caller decides whether to treat as
        /// "row-not-drillable" or to throw. Used by drill-through to
        /// convert a result-row dimension cell into a filter range.
        /// </summary>
        public static bool TryParseLabel(
            string? label, DateHierarchy hierarchy,
            out DateTime start, out DateTime endExclusive)
        {
            start = default;
            endExclusive = default;
            if (string.IsNullOrWhiteSpace(label)) { return false; }
            try
            {
                switch (hierarchy)
                {
                    case DateHierarchy.Year:
                        if (!int.TryParse(label, out var y)) { return false; }
                        start = new DateTime(y, 1, 1);
                        endExclusive = start.AddYears(1);
                        return true;

                    case DateHierarchy.Quarter:
                        // "2026 Q1" → year=2026, quarter=1 → Jan–Mar
                        var qParts = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (qParts.Length != 2 || !qParts[1].StartsWith("Q")) { return false; }
                        if (!int.TryParse(qParts[0], out var qy)) { return false; }
                        if (!int.TryParse(qParts[1].AsSpan(1), out var q)) { return false; }
                        if (q < 1 || q > 4) { return false; }
                        start = new DateTime(qy, (q - 1) * 3 + 1, 1);
                        endExclusive = start.AddMonths(3);
                        return true;

                    case DateHierarchy.Month:
                        // "2026-03"
                        var mParts = label.Split('-');
                        if (mParts.Length != 2) { return false; }
                        if (!int.TryParse(mParts[0], out var my)) { return false; }
                        if (!int.TryParse(mParts[1], out var mm)) { return false; }
                        if (mm < 1 || mm > 12) { return false; }
                        start = new DateTime(my, mm, 1);
                        endExclusive = start.AddMonths(1);
                        return true;

                    case DateHierarchy.Day:
                        // "2026-03-15"
                        if (!DateTime.TryParseExact(label, "yyyy-MM-dd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var d))
                        { return false; }
                        start = d;
                        endExclusive = d.AddDays(1);
                        return true;

                    default:
                        return false;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                return false; // out-of-range year / month / day — invalid input
            }
        }

        private static Expression BuildQuarterKey(Expression dateExpr)
        {
            var year = Expression.Property(dateExpr, nameof(DateTime.Year));
            var month = Expression.Property(dateExpr, nameof(DateTime.Month));
            // (month - 1) / 3 + 1
            var quarter = Expression.Add(
                Expression.Divide(
                    Expression.Subtract(month, Expression.Constant(1)),
                    Expression.Constant(3)),
                Expression.Constant(1));
            // year * 10 + quarter
            return Expression.Add(
                Expression.Multiply(year, Expression.Constant(10)),
                quarter);
        }

        private static Expression BuildMonthKey(Expression dateExpr)
        {
            var year = Expression.Property(dateExpr, nameof(DateTime.Year));
            var month = Expression.Property(dateExpr, nameof(DateTime.Month));
            return Expression.Add(
                Expression.Multiply(year, Expression.Constant(100)),
                month);
        }

        private static Expression BuildDayKey(Expression dateExpr)
        {
            var year = Expression.Property(dateExpr, nameof(DateTime.Year));
            var month = Expression.Property(dateExpr, nameof(DateTime.Month));
            var day = Expression.Property(dateExpr, nameof(DateTime.Day));
            return Expression.Add(
                Expression.Add(
                    Expression.Multiply(year, Expression.Constant(10000)),
                    Expression.Multiply(month, Expression.Constant(100))),
                day);
        }
    }
}
