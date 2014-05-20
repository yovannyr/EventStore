using System.Security.Principal;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication
{
    [TestFixture]
    public class when_handling_request_with_wrong_password :
        with_internal_authentication_provider
    {
        private bool _unauthorized;
        private IPrincipal _authenticatedAs;

        protected override void Given()
        {
            base.Given();
            ExistingEvent("$user-user", "$user", null, "{LoginName:'user', Salt:'drowssap',Hash:'password'}");
        }

        [SetUp]
        public void SetUp()
        {
            SetUpProvider();

            _internalAuthenticationProvider.Authenticate(
                new TestAuthenticationRequest(
                    "user",
                    "password1",
                    () => _unauthorized = true,
                    p => _authenticatedAs = p,
                    () => { }));
        }

        [Test]
        public void does_not_authenticate_user()
        {
            Assert.IsTrue(_unauthorized);
            Assert.IsNull(_authenticatedAs);
        }

    }
}