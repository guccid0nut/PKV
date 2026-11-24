// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

namespace MERRICK.DatabaseContext.Queries;

/// <summary>
///     Contains pre-compiled Entity Framework Core async queries for hot-path database lookups,
///     bypassing LINQ expression tree compilation on every query execution.
/// </summary>
public static class CompiledQueries
{
    /// <summary>
    ///     Deprecated query. Session cookies are validated exclusively via Redis distributed cache.
    /// </summary>
    [Obsolete("Session cookies are validated exclusively via Redis distributed cache.")]
    public static readonly Func<MerrickContext, string, string?, Task<Account?>> GetAccountByCookieQuery =
        (db, cookie, altCookie) => Task.FromResult<Account?>(null);

    /// <summary>
    ///     Pre-compiled query for account lookup by name (SARGable index seek on IX_Accounts_Name).
    /// </summary>
    public static readonly Func<MerrickContext, string, Task<Account?>> GetAccountByNameQuery =
        EF.CompileAsyncQuery((MerrickContext db, string name) =>
            db.Accounts
                .AsNoTracking()
                .FirstOrDefault(a => a.Name == name));

    /// <summary>
    ///     Pre-compiled query for account lookup by ID.
    /// </summary>
    public static readonly Func<MerrickContext, int, Task<Account?>> GetAccountByIdQuery =
        EF.CompileAsyncQuery((MerrickContext db, int id) =>
            db.Accounts
                .AsNoTracking()
                .FirstOrDefault(a => a.ID == id));

    /// <summary>
    ///     Pre-compiled query for account lookup by name including clan navigation.
    /// </summary>
    public static readonly Func<MerrickContext, string, Task<Account?>> GetAccountByNameWithClanQuery =
        EF.CompileAsyncQuery((MerrickContext db, string name) =>
            db.Accounts
                .Include(a => a.Clan)
                .AsNoTracking()
                .FirstOrDefault(a => a.Name == name));

    /// <summary>
    ///     Pre-compiled query for account lookup by ID including clan navigation.
    /// </summary>
    public static readonly Func<MerrickContext, int, Task<Account?>> GetAccountByIdWithClanQuery =
        EF.CompileAsyncQuery((MerrickContext db, int id) =>
            db.Accounts
                .Include(a => a.Clan)
                .AsNoTracking()
                .FirstOrDefault(a => a.ID == id));

    /// <summary>
    ///     Pre-compiled query for clan lookup by name.
    /// </summary>
    public static readonly Func<MerrickContext, string, Task<Clan?>> GetClanByNameQuery =
        EF.CompileAsyncQuery((MerrickContext db, string name) =>
            db.Clans
                .AsNoTracking()
                .FirstOrDefault(c => c.Name == name));

    /// <summary>
    ///     Pre-compiled query for user lookup by email.
    /// </summary>
    public static readonly Func<MerrickContext, string, Task<User?>> GetUserByEmailQuery =
        EF.CompileAsyncQuery((MerrickContext db, string email) =>
            db.Users
                .AsNoTracking()
                .FirstOrDefault(u => u.EmailAddress == email));

    /// <summary>
    ///     Pre-compiled query for account lookup by name including user and clan navigations.
    /// </summary>
    public static readonly Func<MerrickContext, string, Task<Account?>> GetAccountByNameWithUserAndClanQuery =
        EF.CompileAsyncQuery((MerrickContext db, string name) =>
            db.Accounts
                .Include(a => a.User)
                .Include(a => a.Clan)
                .AsNoTracking()
                .FirstOrDefault(a => a.Name == name));

    /// <summary>
    ///     Pre-compiled query for tracked account lookup by name including user and clan navigations.
    /// </summary>
    public static readonly Func<MerrickContext, string, Task<Account?>> GetAccountByNameTrackingWithUserAndClanQuery =
        EF.CompileAsyncQuery((MerrickContext db, string name) =>
            db.Accounts
                .Include(a => a.User)
                .Include(a => a.Clan)
                .FirstOrDefault(a => a.Name == name));

    /// <summary>
    ///     Pre-compiled query checking if an account with the specified name exists.
    /// </summary>
    public static readonly Func<MerrickContext, string, Task<bool>> AccountExistsByNameQuery =
        EF.CompileAsyncQuery((MerrickContext db, string name) =>
            db.Accounts.Any(a => a.Name == name));

    /// <summary>
    ///     Pre-compiled query checking if a user with the specified email exists.
    /// </summary>
    public static readonly Func<MerrickContext, string, Task<bool>> UserExistsByEmailQuery =
        EF.CompileAsyncQuery((MerrickContext db, string email) =>
            db.Users.Any(u => u.EmailAddress == email));

    /// <summary>
    ///     Pre-compiled query for tracked user lookup by email including accounts navigation.
    /// </summary>
    public static readonly Func<MerrickContext, string, Task<User?>> GetUserByEmailWithAccountsTrackingQuery =
        EF.CompileAsyncQuery((MerrickContext db, string email) =>
            db.Users
                .Include(u => u.Accounts)
                .FirstOrDefault(u => u.EmailAddress == email));

    /// <summary>
    ///     Pre-compiled query for account lookup with User and Statistics navigations.
    /// </summary>
    public static readonly Func<MerrickContext, string, Task<Account?>> GetAccountByNameWithStatsAndUserQuery =
        EF.CompileAsyncQuery((MerrickContext db, string name) =>
            db.Accounts
                .Include(a => a.User)
                .Include(a => a.Statistics)
                .AsNoTracking()
                .FirstOrDefault(a => a.Name == name));

    /// <summary>
    ///     Pre-compiled query for system telemetry metrics lookup by ID.
    /// </summary>
    public static readonly Func<MerrickContext, int, Task<SystemTelemetryMetrics?>> GetTelemetryMetricsById =
        EF.CompileAsyncQuery((MerrickContext db, int id) =>
            db.SystemTelemetryMetrics
                .AsNoTracking()
                .FirstOrDefault(m => m.ID == id));
}
