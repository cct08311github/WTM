using System;
using System.Collections.Generic;
using System.Text;

namespace WalkingTec.Mvvm.Core
{
    public class Cors
    {
        public bool EnableAll { get; set; }
        public List<CorsPolicy> Policy { get; set; }

        /// <summary>
        /// Only applies when no <see cref="Policy"/> is configured and the built-in default policy is used.
        /// true (default, unchanged behavior): any origin is allowed and credentials (cookies, Authorization headers)
        /// are allowed, which makes the browser reflect the caller's Origin back with credentials.
        /// false: any origin is still allowed, but without credentials ("Access-Control-Allow-Origin: *").
        /// Configure an explicit <see cref="Policy"/> to restrict origins.
        /// </summary>
        public bool AllowCredentialsWithAnyOrigin { get; set; } = true;
    }

    public class CorsPolicy
    {
        public string Name { get; set; }
        public string Domain { get; set; }
    }
}
