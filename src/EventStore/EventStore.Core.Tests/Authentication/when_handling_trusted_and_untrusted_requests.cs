using System.Security.Principal;
using NUnit.Framework;

namespace EventStore.Core.Tests.Authentication
{
    [TestFixture]
    public class when_handling_trusted_and_untrusted_requests :
        with_internal_authentication_provider
    {
        private bool _unauthorized;
        private IPrincipal _authenticatedAs;
        private bool _error;

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
                new TestAuthenticationRequest("user", () => { }, p => { }, () => { }));

            _consumer.HandledMessages.Clear();

            _internalAuthenticationProvider.Authenticate(
                new TestAuthenticationRequest(
                    "user", "password", () => _unauthorized = true, p => _authenticatedAs = p, () => _error = true));
        }

        [Test]
        public void authenticates_user()
        {
            Assert.IsFalse(_unauthorized);
            Assert.IsFalse(_error);
            Assert.NotNull(_authenticatedAs);
            Assert.IsTrue(_authenticatedAs.IsInRole("user"));
        }

    }
}