#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core
{
    public class CS
    {
        public string? Key { get; set; }
        public string? Value { get; set; }
        public DBTypeEnum? DbType { get; set; }
        public string? Version { get; set; }
        public string? DbContext { get; set; }

        /// <summary>
        /// Whether this connection is active. Defaults to true for backward compatibility.
        /// Set to false in appsettings.json to disable a connection without removing it —
        /// useful for graceful degradation when a secondary database (e.g. Oracle) is unreachable.
        /// </summary>
        public bool Enabled { get; set; } = true;

        public ConstructorInfo? DcConstructor;
        private static List<ConstructorInfo>? _cis;
        public static List<ConstructorInfo> Cis
        {
            get
            {
                // #538: populate into a local list and publish atomically at the end — a concurrent
                // reader must never observe a partially-populated static list mid-loop.
                if (_cis == null)
                {
                    var AllAssembly = Utils.GetAllAssembly();
                    var cis = new List<ConstructorInfo>();
                    if (AllAssembly != null)
                    {
                        foreach (var ass in AllAssembly)
                        {
                            try
                            {
                                List<Type> t = [.. ass.GetExportedTypes().Where(x => typeof(DbContext).IsAssignableFrom(x) && x.Name != "DbContext" && x.Name != "FrameworkContext" && x.Name != "EmptyContext")];
                                foreach (var st in t)
                                {
                                    var ci = st.GetConstructor(new Type[] { typeof(CS) });
                                    if (ci != null)
                                    {
                                        cis.Add(ci);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                CoreProgram.GetLogger("CS")?.LogDebug(ex, "CS.Cis: scanning assembly '{Asm}' for DbContext(CS) constructors threw; skipping", ass.FullName);
                            }
                        }
                    }
                    _cis = cis;
                }
                return _cis;
            }
        }

        private static List<ConstructorInfo>? _cisFull;
        public static List<ConstructorInfo> CisFull
        {
            get
            {
                // #538: populate into a local list and publish atomically at the end — a concurrent
                // reader must never observe a partially-populated static list mid-loop.
                if (_cisFull == null)
                {
                    var AllAssembly = Utils.GetAllAssembly();
                    var cisFull = new List<ConstructorInfo>();
                    if (AllAssembly != null)
                    {
                        foreach (var ass in AllAssembly)
                        {
                            try
                            {
                                List<Type> t = [.. ass.GetExportedTypes().Where(x => typeof(DbContext).IsAssignableFrom(x) && x.Name != "DbContext" && x.Name != "FrameworkContext" && x.Name != "EmptyContext")];
                                foreach (var st in t)
                                {
                                    var ci = st.GetConstructor(new Type[] { typeof(string), typeof(DBTypeEnum) });
                                    if (ci != null)
                                    {
                                        cisFull.Add(ci);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                CoreProgram.GetLogger("CS")?.LogDebug(ex, "CS.CisFull: scanning assembly '{Asm}' for DbContext(string,DBTypeEnum) constructors threw; skipping", ass.FullName);
                            }
                        }
                    }
                    _cisFull = cisFull;
                }
                return _cisFull;
            }
        }

        public IDataContext? CreateDC()
        {
            if (DcConstructor == null)
            {
                string? dcname = DbContext;
                if (string.IsNullOrEmpty(dcname))
                {
                    dcname = "DataContext";
                }
                DcConstructor = Cis.Where(x => x.DeclaringType?.Name.ToLower() == dcname.ToLower()).FirstOrDefault();
                if (DcConstructor == null)
                {
                    DcConstructor = Cis.FirstOrDefault();
                }
            }
            return (IDataContext?)DcConstructor?.Invoke(new object[] { this });
        }
    }
}
