#nullable enable
// #290 backstop (C): provider-deadlock victim classifier.
//
// Detects whether an exception is a database deadlock victim signal.
// Provider discrimination is done dependency-free (reflected Number/SqlState +
// exception type-name), mirroring the UNIQUE-collision detection style in
// GuardedTransition.cs:356-361.
//
// Covered codes:
//   SqlServer  — SqlException.Number == 1205
//   PgSql      — NpgsqlException.SqlState == "40P01" or "40001"
//   MySql      — MySqlException.Number == 1213 (ER_LOCK_DEADLOCK)
//   Oracle     — OracleException.Number == 60 (ORA-00060)
//   DaMeng     — UNCONFIRMED under #270; degrades to status-quo rethrow.
//
// IMPORTANT: The DaMeng deadlock code must be confirmed against a live DaMeng
// instance under #270 before the classifier claims DaMeng coverage.  Until then a
// DaMeng deadlock propagates as a raw exception — exactly the same as before #290.
// This is an explicit, honest limitation; do not guess the DaMeng code.

using System;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Classifies a database exception as a provider-level deadlock victim signal.
/// Dependency-free: works by reflection so the engine assembly does not gain
/// hard references to SqlClient / Npgsql / Pomelo / Oracle / Dm packages.
/// </summary>
internal static class WorkflowDeadlockClassifier
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> is a provider deadlock victim
    /// exception on a known server provider.
    ///
    /// <para>Returns <c>false</c> on SQLite (no multi-writer deadlock),
    /// for <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> carrying a
    /// UNIQUE-index violation, and for any exception whose provider code is not in the
    /// confirmed set above.</para>
    /// </summary>
    internal static bool IsDeadlockVictim(Exception ex)
    {
        if (ex is null) return false;

        // Unwrap EF DbUpdateException wrapper — the inner exception carries the provider code.
        var inner = ex.InnerException ?? ex;

        return IsSqlServerDeadlock(inner)
            || IsPgSqlDeadlock(inner)
            || IsMySqlDeadlock(inner)
            || IsOracleDeadlock(inner);
        // DaMeng: code unconfirmed under #270 — intentionally omitted.
        // When #270 wires a live DaMeng instance, add IsDaMengDeadlock() here.
    }

    // ── Provider-specific matchers ────────────────────────────────────────────

    private static bool IsSqlServerDeadlock(Exception ex)
    {
        // Microsoft.Data.SqlClient.SqlException or System.Data.SqlClient.SqlException
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (!typeName.Contains("SqlException", StringComparison.Ordinal)) return false;

        // SqlException.Number == 1205 (deadlock victim)
        return ReadIntProperty(ex, "Number") == 1205;
    }

    private static bool IsPgSqlDeadlock(Exception ex)
    {
        // Npgsql.NpgsqlException — SqlState property
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (!typeName.Contains("NpgsqlException", StringComparison.Ordinal)) return false;

        var sqlState = ReadStringProperty(ex, "SqlState");
        // 40P01 = deadlock detected; 40001 = serialization failure (treated as retryable)
        return sqlState is "40P01" or "40001";
    }

    private static bool IsMySqlDeadlock(Exception ex)
    {
        // MySql.Data.MySqlClient.MySqlException or MySqlConnector.MySqlException
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (!typeName.Contains("MySqlException", StringComparison.Ordinal)) return false;

        // ER_LOCK_DEADLOCK = 1213
        return ReadIntProperty(ex, "Number") == 1213;
    }

    private static bool IsOracleDeadlock(Exception ex)
    {
        // Oracle.ManagedDataAccess.Client.OracleException or Oracle.DataAccess.Client.OracleException
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (!typeName.Contains("OracleException", StringComparison.Ordinal)) return false;

        // ORA-00060 = deadlock detected while waiting for resource
        return ReadIntProperty(ex, "Number") == 60;
    }

    // ── Reflection helpers ────────────────────────────────────────────────────

    private static int? ReadIntProperty(Exception ex, string propertyName)
    {
        try
        {
            var prop = ex.GetType().GetProperty(propertyName);
            if (prop is null) return null;
            var val = prop.GetValue(ex);
            if (val is int i) return i;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadStringProperty(Exception ex, string propertyName)
    {
        try
        {
            var prop = ex.GetType().GetProperty(propertyName);
            return prop?.GetValue(ex) as string;
        }
        catch
        {
            return null;
        }
    }
}
