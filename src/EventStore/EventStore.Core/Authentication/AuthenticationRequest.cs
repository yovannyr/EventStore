using System.Security.Principal;

namespace EventStore.Core.Authentication
{
	public abstract class AuthenticationRequest
	{
		public readonly string Name;
		public readonly string SuppliedPassword;
	    public readonly bool TrustedWithoutPassword;

		protected AuthenticationRequest(string name, string suppliedPassword)
		{
			Name = name;
			SuppliedPassword = suppliedPassword;
		}

        protected AuthenticationRequest(string name, bool trustedWithoutPassword = false)
        {
            Name = name;
            TrustedWithoutPassword = trustedWithoutPassword;
        }

        public abstract void Unauthorized();
		public abstract void Authenticated(IPrincipal principal);
		public abstract void Error();
	}
}