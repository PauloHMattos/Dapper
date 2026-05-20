using System;
using System.Data;
using System.Linq;
using Xunit;

namespace Dapper.Tests
{
    /// <summary>
    /// Tests for <see cref="SqlMapper.Settings.UseTypedAccessors"/> with the default setting (false).
    /// Verifies that existing behaviour is unchanged when the optimization is disabled.
    /// </summary>
    [Collection(NonParallelDefinition.Name)]
    public sealed class TypedAccessorDefaultTests : TypedAccessorTestsBase
    {
        public TypedAccessorDefaultTests() : base(useTypedAccessors: false) { }
    }

    /// <summary>
    /// Tests for <see cref="SqlMapper.Settings.UseTypedAccessors"/> with the optimization enabled (true).
    /// Verifies that typed accessor code paths produce identical results to the generic GetValue path.
    /// </summary>
    [Collection(NonParallelDefinition.Name)]
    public sealed class TypedAccessorEnabledTests : TypedAccessorTestsBase
    {
        public TypedAccessorEnabledTests() : base(useTypedAccessors: true) { }
    }

    /// <summary>
    /// Shared test logic for <see cref="SqlMapper.Settings.UseTypedAccessors"/>.
    /// Inheriting from <see cref="SqliteTypeTestBase"/> so the tests run without any external
    /// database dependencies (SQLite is always available in the test project).
    /// Each test wraps its body in <see cref="WithSetting"/> to activate the desired setting,
    /// purge the deserializer cache, and restore the original setting on exit.
    /// </summary>
    public abstract class TypedAccessorTestsBase : SqliteTypeTestBase
    {
        private readonly bool _useTypedAccessors;

        protected TypedAccessorTestsBase(bool useTypedAccessors)
            => _useTypedAccessors = useTypedAccessors;

        /// <summary>
        /// Temporarily applies <see cref="SqlMapper.Settings.UseTypedAccessors"/> for the duration
        /// of <paramref name="action"/>, then restores the previous value and purges the cache.
        /// </summary>
        private void WithSetting(Action action)
        {
            bool old = SqlMapper.Settings.UseTypedAccessors;
            SqlMapper.Settings.UseTypedAccessors = _useTypedAccessors;
            SqlMapper.PurgeQueryCache();
            try
            {
                action();
            }
            finally
            {
                SqlMapper.Settings.UseTypedAccessors = old;
                SqlMapper.PurgeQueryCache();
            }
        }

        // ── DTOs ────────────────────────────────────────────────────────────────

        private class IntResult { public int Id { get; set; } }
        private class LongResult { public long Id { get; set; } }
        private class StringResult { public string? Name { get; set; } }
        private class NullableIntResult { public int? Value { get; set; } }
        private class MultiColumnResult
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public long BigId { get; set; }
        }

        // ── Tests ────────────────────────────────────────────────────────────────

        /// <summary>
        /// SQLite returns <see cref="long"/> for integer literals.
        /// A narrowing conversion to <see cref="int"/> must succeed for values that fit.
        /// </summary>
        [FactSqlite]
        public void TypedAccessors_BasicInt_Read()
        {
            WithSetting(() =>
            {
                using var conn = GetSQLiteConnection();
                var result = conn.QueryFirst<IntResult>("SELECT 42 AS Id");
                Assert.Equal(42, result.Id);
            });
        }

        [FactSqlite]
        public void TypedAccessors_BasicLong_Read()
        {
            WithSetting(() =>
            {
                using var conn = GetSQLiteConnection();
                var result = conn.QueryFirst<LongResult>("SELECT 42 AS Id");
                Assert.Equal(42L, result.Id);
            });
        }

        [FactSqlite]
        public void TypedAccessors_BasicString_Read()
        {
            WithSetting(() =>
            {
                using var conn = GetSQLiteConnection();
                var result = conn.QueryFirst<StringResult>("SELECT 'hello' AS Name");
                Assert.Equal("hello", result.Name);
            });
        }

        /// <summary>
        /// DB column is <see cref="long"/> (SQLite INTEGER), .NET member is <see cref="int"/>.
        /// A checked narrowing conversion (<c>Conv_Ovf_I4</c>) must succeed for in-range values.
        /// </summary>
        [FactSqlite]
        public void TypedAccessors_DBLong_To_DotNetInt_Narrowing()
        {
            WithSetting(() =>
            {
                using var conn = GetSQLiteConnection();
                var result = conn.QueryFirst<IntResult>("SELECT 1000000 AS Id");
                Assert.Equal(1000000, result.Id);
            });
        }

        /// <summary>
        /// DB column is <see cref="long"/> (SQLite INTEGER) with a value that overflows <see cref="int"/>.
        /// The generated <c>Conv_Ovf_I4</c> instruction must cause an <see cref="OverflowException"/>
        /// wrapped in a <see cref="DataException"/> by Dapper's standard error handler.
        /// </summary>
        [FactSqlite]
        public void TypedAccessors_DBLong_To_DotNetInt_Overflow_Throws()
        {
            WithSetting(() =>
            {
                using var conn = GetSQLiteConnection();
                // 99999999999 > int.MaxValue; the checked conversion must throw.
                Assert.Throws<DataException>(() => conn.QueryFirst<IntResult>("SELECT 99999999999 AS Id"));
            });
        }

        /// <summary>
        /// Nullable column with a NULL value must map to <see langword="null"/>.
        /// </summary>
        [FactSqlite]
        public void TypedAccessors_NullableInt_WithNull()
        {
            WithSetting(() =>
            {
                using var conn = GetSQLiteConnection();
                var result = conn.QueryFirst<NullableIntResult>("SELECT NULL AS Value");
                Assert.Null(result.Value);
            });
        }

        /// <summary>
        /// Nullable column with a non-NULL value must map to the expected integer.
        /// </summary>
        [FactSqlite]
        public void TypedAccessors_NullableInt_WithValue()
        {
            WithSetting(() =>
            {
                using var conn = GetSQLiteConnection();
                var result = conn.QueryFirst<NullableIntResult>("SELECT 99 AS Value");
                Assert.Equal(99, result.Value);
            });
        }

        [FactSqlite]
        public void TypedAccessors_MultipleColumns_Read()
        {
            WithSetting(() =>
            {
                using var conn = GetSQLiteConnection();
                var result = conn.QueryFirst<MultiColumnResult>("SELECT 1 AS Id, 'test' AS Name, 9999999999 AS BigId");
                Assert.Equal(1, result.Id);
                Assert.Equal("test", result.Name);
                Assert.Equal(9999999999L, result.BigId);
            });
        }

        /// <summary>
        /// A schema-declared NOT NULL column is safe to skip the IsDBNull check.
        /// The setting still produces the correct value when <c>GetColumnAllowsDBNull</c> returns false.
        /// </summary>
        [FactSqlite]
        public void TypedAccessors_NotNullColumn_SkipsIsDBNullCheck()
        {
            WithSetting(() =>
            {
                using var conn = GetSQLiteConnection();
                conn.Execute("CREATE TABLE IF NOT EXISTS ta_notnull (Id INTEGER NOT NULL)");
                try
                {
                    conn.Execute("INSERT INTO ta_notnull (Id) VALUES (77)");
                    var result = conn.QueryFirst<IntResult>("SELECT Id FROM ta_notnull");
                    Assert.Equal(77, result.Id);
                }
                finally
                {
                    conn.Execute("DROP TABLE IF EXISTS ta_notnull");
                }
            });
        }

        /// <summary>
        /// A schema-declared nullable column must correctly return both NULL and non-NULL rows.
        /// </summary>
        [FactSqlite]
        public void TypedAccessors_NullableColumn_HandlesNull()
        {
            WithSetting(() =>
            {
                using var conn = GetSQLiteConnection();
                conn.Execute("CREATE TABLE IF NOT EXISTS ta_nullable (Value INTEGER)");
                try
                {
                    conn.Execute("INSERT INTO ta_nullable (Value) VALUES (NULL)");
                    conn.Execute("INSERT INTO ta_nullable (Value) VALUES (55)");
                    var results = conn.Query<NullableIntResult>("SELECT Value FROM ta_nullable ORDER BY rowid").ToList();
                    Assert.Equal(2, results.Count);
                    Assert.Null(results[0].Value);
                    Assert.Equal(55, results[1].Value);
                }
                finally
                {
                    conn.Execute("DROP TABLE IF EXISTS ta_nullable");
                }
            });
        }

        /// <summary>
        /// Value-tuple deserialization must work correctly with both settings.
        /// </summary>
        [FactSqlite]
        public void TypedAccessors_ValueTuple_Read()
        {
            WithSetting(() =>
            {
                using var conn = GetSQLiteConnection();
                var result = conn.QueryFirst<(int Id, string Name)>("SELECT 7 AS Id, 'foo' AS Name");
                Assert.Equal(7, result.Id);
                Assert.Equal("foo", result.Name);
            });
        }
    }
}
