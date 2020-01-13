﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Abp.BackgroundJobs;
using Abp.Json;
using Abp.Threading;
using Abp.Webhooks;
using Abp.Webhooks.BackgroundWorker;
using Abp.Zero.SampleApp.Application;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Abp.Zero.SampleApp.Tests.Webhooks
{
    public class WebhookPublisher_Tests : WebhookTestBase
    {
        private readonly IWebhookPublisher _webhookPublisher;
        private readonly IBackgroundJobManager _backgroundJobManagerSubstitute;

        public WebhookPublisher_Tests()
        {
            AbpSession.UserId = 1;
            AbpSession.TenantId = null;

            _backgroundJobManagerSubstitute = RegisterFake<IBackgroundJobManager>();
            _webhookPublisher = Resolve<IWebhookPublisher>();
        }

        #region Async
        /// <summary>
        /// Creates tenant with adding feature(s), then creates predicate for WebhookSenderInput which publisher should send to WebhookSenderJob
        /// </summary>
        /// <param name="webhookDefinition"></param>
        /// <param name="tenantFeatures"></param>
        /// <returns></returns>
        private async Task<(int? tenantId, object data, Predicate<WebhookSenderInput> predicate)> InitializeTestCase(string webhookDefinition, Dictionary<string, string> tenantFeatures)
        {
            var subscription = await CreateTenantAndSubscribeToWebhookAsync(webhookDefinition, tenantFeatures);

            var webhooksConfiguration = Resolve<IWebhooksConfiguration>();

            var data = new { Name = "Musa", Surname = "Demir" };

            Predicate<WebhookSenderInput> predicate = w =>
            {
                w.Secret.ShouldNotBeNullOrEmpty();
                w.Secret.ShouldStartWith("whs_");
                w.WebhookDefinition.ShouldContain(webhookDefinition);

                w.Headers.Count.ShouldBe(1);
                w.Headers.Single().Key.ShouldBe("Key");
                w.Headers.Single().Value.ShouldBe("Value");

                w.WebhookSubscriptionId.ShouldBe(subscription.Id);
                w.Data.ShouldBe(
                    webhooksConfiguration.JsonSerializerSettings != null
                        ? data.ToJsonString(webhooksConfiguration.JsonSerializerSettings)
                        : data.ToJsonString()
                );
                return true;
            };

            return (subscription.TenantId, data, predicate);
        }

        [Fact]
        public async Task Should_Not_Send_Webhook_If_There_Is_No_Subscription_Async()
        {
            await CreateTenantAndSubscribeToWebhookAsync(AppWebhookDefinitionNames.Users.Created,
               AppFeatures.WebhookFeature, "true");

            AbpSession.TenantId = GetDefaultTenant().Id;

            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Test,
                new
                {
                    Name = "Musa",
                    Surname = "Demir"
                });

            await _backgroundJobManagerSubstitute.DidNotReceive().EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Any<WebhookSenderInput>());
        }

        [Fact]
        public async Task Should_Send_Webhook_To_Authorized_Tenant_Async()
        {
            var (tenantId, data, predicate) = await InitializeTestCase(AppWebhookDefinitionNames.Users.Created,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"}
                });

            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Users.Created, data, tenantId);

            await _backgroundJobManagerSubstitute.Received()
                .EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate(w)));
        }

        [Fact]
        public async Task Should_Send_Webhook_To_Authorized_Current_Tenant_Async()
        {
            var (tenantId, data, predicate) = await InitializeTestCase(AppWebhookDefinitionNames.Users.Created,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"}
                });

            AbpSession.TenantId = tenantId;

            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Users.Created, data);

            await _backgroundJobManagerSubstitute.Received()
                .EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate(w)));
        }

        [Fact]
        public async Task Should_Not_Send_Webhook_To_Tenant_If_Features_Are_Not_Granted_Async()
        {
            var subscription = await CreateTenantAndSubscribeToWebhookAsync(AppWebhookDefinitionNames.Users.Created,
                AppFeatures.WebhookFeature, "true");

            await AddOrReplaceFeatureToTenantAsync(subscription.TenantId.Value, AppFeatures.WebhookFeature, "false");

            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Users.Created, new { Name = "Musa", Surname = "Demir" }, subscription.TenantId);

            //should not try to send
            await _backgroundJobManagerSubstitute.DidNotReceive().EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Any<WebhookSenderInput>());
        }

        [Fact]
        public async Task Should_Not_Send_Webhook_To_Current_Tenant_If_Features_Are_Not_Granted_Async()
        {
            var subscription = await CreateTenantAndSubscribeToWebhookAsync(AppWebhookDefinitionNames.Users.Created,
                AppFeatures.WebhookFeature, "true");

            AbpSession.TenantId = subscription.TenantId;

            await AddOrReplaceFeatureToTenantAsync(AbpSession.TenantId.Value, AppFeatures.WebhookFeature, "false");

            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Users.Created, new { Name = "Musa", Surname = "Demir" });

            //should not try to send
            await _backgroundJobManagerSubstitute.DidNotReceive().EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Any<WebhookSenderInput>());
        }

        [Fact]
        public async Task Should_Send_Webhook_To_Tenant_If_All_Required_Features_Granted_Async()
        {
            //user_deleted webhook requires AppFeatures.WebhookFeature, AppFeatures.TestFeature but not requires all
            var (tenantId, data, predicate) = await InitializeTestCase(AppWebhookDefinitionNames.Users.Deleted,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"}
                });

            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Users.Deleted, data, tenantId);

            await _backgroundJobManagerSubstitute.Received()
                .EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate(w)));

            _backgroundJobManagerSubstitute.ClearReceivedCalls();

            //DefaultThemeChanged webhook requires AppFeatures.WebhookFeature, AppFeatures.ThemeFeature and requires all
            var (tenantId2, data2, predicate2) = await InitializeTestCase(AppWebhookDefinitionNames.Theme.DefaultThemeChanged,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"},
                    {AppFeatures.ThemeFeature, "true"}
                });

            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Theme.DefaultThemeChanged, data2, tenantId2);

            await _backgroundJobManagerSubstitute.Received()
                .EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate2(w)));
        }

        [Fact]
        public async Task Should_Send_Webhook_To_Current_Tenant_If_All_Required_Features_Granted_Async()
        {
            //user_deleted webhook requires AppFeatures.WebhookFeature, AppFeatures.TestFeature but not requires all
            var (tenantId, data, predicate) = await InitializeTestCase(AppWebhookDefinitionNames.Users.Deleted,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"}
                });

            AbpSession.TenantId = tenantId;
            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Users.Deleted, data);

            await _backgroundJobManagerSubstitute.Received()
                .EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate(w)));

            _backgroundJobManagerSubstitute.ClearReceivedCalls();

            //DefaultThemeChanged webhook requires AppFeatures.WebhookFeature, AppFeatures.ThemeFeature and requires all
            var (tenantId2, data2, predicate2) = await InitializeTestCase(AppWebhookDefinitionNames.Theme.DefaultThemeChanged,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"},
                    {AppFeatures.ThemeFeature, "true"}
                });

            AbpSession.TenantId = tenantId2;
            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Theme.DefaultThemeChanged, data2);

            await _backgroundJobManagerSubstitute.Received()
                .EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate2(w)));
        }

        [Fact]
        public async Task Should_Not_Send_Webhook_To_If_Tenant_Does_Not_Have_All_Features_When_Its_Required_All_Async()
        {
            //DefaultThemeChanged webhook requires AppFeatures.WebhookFeature, AppFeatures.ThemeFeature and requires all
            var subscription = await CreateTenantAndSubscribeToWebhookAsync(AppWebhookDefinitionNames.Theme.DefaultThemeChanged,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"},
                    {AppFeatures.ThemeFeature, "true"}
                });

            //remove one feature
            await AddOrReplaceFeatureToTenantAsync(subscription.TenantId.Value, AppFeatures.WebhookFeature, "false");

            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Theme.DefaultThemeChanged, new { Name = "Musa", Surname = "Demir" }, subscription.TenantId);

            //should not try to send
            await _backgroundJobManagerSubstitute.DidNotReceive().EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Any<WebhookSenderInput>());
        }

        [Fact]
        public async Task Should_Not_Send_Webhook_To_If_Current_Tenant_Does_Not_Have_All_Features_When_Its_Required_All_Async()
        {
            //DefaultThemeChanged webhook requires AppFeatures.WebhookFeature, AppFeatures.ThemeFeature and requires all
            var subscription = await CreateTenantAndSubscribeToWebhookAsync(AppWebhookDefinitionNames.Theme.DefaultThemeChanged,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"},
                    {AppFeatures.ThemeFeature, "true"}
                });

            //remove one feature
            await AddOrReplaceFeatureToTenantAsync(subscription.TenantId.Value, AppFeatures.WebhookFeature, "false");

            AbpSession.TenantId = subscription.TenantId;
            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Theme.DefaultThemeChanged, new { Name = "Musa", Surname = "Demir" });

            //should not try to send
            await _backgroundJobManagerSubstitute.DidNotReceive().EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Any<WebhookSenderInput>());
        }

        [Fact]
        public async Task Should_Send_Webhook_To_Host_If_Subscribed_Async()
        {
            var subscription = new WebhookSubscription
            {
                TenantId = null,
                Secret = "secret",
                WebhookUri = "www.mywebhook.com",
                WebhookDefinitions = new List<string>() { AppWebhookDefinitionNames.Users.Created },
                Headers = new Dictionary<string, string>
                {
                    { "Key","Value"}
                }
            };

            var webhookSubscriptionManager = Resolve<IWebhookSubscriptionManager>();
            var webhooksConfiguration = Resolve<IWebhooksConfiguration>();

            await webhookSubscriptionManager.AddOrUpdateSubscriptionAsync(subscription);

            var data = new { Name = "Musa", Surname = "Demir" };

            Predicate<WebhookSenderInput> predicate = w =>
            {
                w.Secret.ShouldNotBeNullOrEmpty();
                w.Secret.ShouldStartWith("whs_");
                w.WebhookDefinition.ShouldContain(AppWebhookDefinitionNames.Users.Created);

                w.Headers.Count.ShouldBe(1);
                w.Headers.Single().Key.ShouldBe("Key");
                w.Headers.Single().Value.ShouldBe("Value");

                w.WebhookSubscriptionId.ShouldBe(subscription.Id);
                w.Data.ShouldBe(
                    webhooksConfiguration.JsonSerializerSettings != null
                        ? data.ToJsonString(webhooksConfiguration.JsonSerializerSettings)
                        : data.ToJsonString()
                );
                return true;
            };

            await _webhookPublisher.PublishAsync(AppWebhookDefinitionNames.Users.Created, data, null);

            await _backgroundJobManagerSubstitute.Received()
                .EnqueueAsync<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate(w)));
        }

        #endregion

        #region Sync

        [Fact]
        public void Should_Not_Send_Webhook_If_There_Is_Subscription_Sync()
        {
            AsyncHelper.RunSync(() => CreateTenantAndSubscribeToWebhookAsync(AppWebhookDefinitionNames.Users.Created,
               AppFeatures.WebhookFeature, "true"));

            AbpSession.TenantId = GetDefaultTenant().Id;
            _webhookPublisher.Publish(AppWebhookDefinitionNames.Test,
               new
               {
                   Name = "Musa",
                   Surname = "Demir"
               });

            _backgroundJobManagerSubstitute.DidNotReceive().Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Any<WebhookSenderInput>());
        }

        [Fact]
        public void Should_Send_Webhook_To_Authorized_Tenant_Sync()
        {
            var (tenantId, data, predicate) = AsyncHelper.RunSync(() => InitializeTestCase(AppWebhookDefinitionNames.Users.Created,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"}
                }));

            _webhookPublisher.Publish(AppWebhookDefinitionNames.Users.Created, data, tenantId);

            _backgroundJobManagerSubstitute.Received()
               .Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate(w)));
        }

        [Fact]
        public void Should_Send_Webhook_To_Authorized_Current_Tenant_Sync()
        {
            var (tenantId, data, predicate) = AsyncHelper.RunSync(() => InitializeTestCase(AppWebhookDefinitionNames.Users.Created,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"}
                }));

            AbpSession.TenantId = tenantId;

            _webhookPublisher.Publish(AppWebhookDefinitionNames.Users.Created, data);

            _backgroundJobManagerSubstitute.Received()
                .Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate(w)));
        }

        [Fact]
        public void Should_Not_Send_Webhook_To_Tenant_Who_Does_Not_Have_Feature_Sync()
        {
            var subscription = AsyncHelper.RunSync(() => CreateTenantAndSubscribeToWebhookAsync(AppWebhookDefinitionNames.Users.Created,
                AppFeatures.WebhookFeature, "true"));

            AsyncHelper.RunSync(() => AddOrReplaceFeatureToTenantAsync(subscription.TenantId.Value, AppFeatures.WebhookFeature, "false"));

            _webhookPublisher.Publish(AppWebhookDefinitionNames.Users.Created, new { Name = "Musa", Surname = "Demir" }, subscription.TenantId);

            //should not try to send
            _backgroundJobManagerSubstitute.DidNotReceive().Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Any<WebhookSenderInput>());
        }

        [Fact]
        public void Should_Not_Send_Webhook_To_Current_Tenant_Who_Does_Not_Have_Feature_Sync()
        {
            var subscription = AsyncHelper.RunSync(() => CreateTenantAndSubscribeToWebhookAsync(AppWebhookDefinitionNames.Users.Created,
                AppFeatures.WebhookFeature, "true"));

            AsyncHelper.RunSync(() => AddOrReplaceFeatureToTenantAsync(subscription.TenantId.Value, AppFeatures.WebhookFeature, "false"));

            AbpSession.TenantId = subscription.TenantId;

            _webhookPublisher.Publish(AppWebhookDefinitionNames.Users.Created, new { Name = "Musa", Surname = "Demir" });

            //should not try to send
            _backgroundJobManagerSubstitute.DidNotReceive().Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Any<WebhookSenderInput>());
        }

        [Fact]
        public void Should_Send_Webhook_To_Tenant_If_All_Required_Features_Granted_Sync()
        {
            //user_deleted webhook requires AppFeatures.WebhookFeature, AppFeatures.TestFeature but not requires all

            var (tenantId, data, predicate) = AsyncHelper.RunSync(() => InitializeTestCase(AppWebhookDefinitionNames.Users.Deleted,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"}
                }));

            _webhookPublisher.Publish(AppWebhookDefinitionNames.Users.Deleted, data, tenantId);

            _backgroundJobManagerSubstitute.Received()
                .Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate(w)));

            _backgroundJobManagerSubstitute.ClearReceivedCalls();

            //DefaultThemeChanged webhook requires AppFeatures.WebhookFeature, AppFeatures.ThemeFeature and requires all
            var (tenantId2, data2, predicate2) = AsyncHelper.RunSync(() => InitializeTestCase(AppWebhookDefinitionNames.Theme.DefaultThemeChanged,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"},
                    {AppFeatures.ThemeFeature, "true"}
                }));

            _webhookPublisher.Publish(AppWebhookDefinitionNames.Theme.DefaultThemeChanged, data2, tenantId2);

            _backgroundJobManagerSubstitute.Received()
               .Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate2(w)));
        }

        [Fact]
        public void Should_Send_Webhook_To_Current_Tenant_If_All_Required_Features_Granted_Sync()
        {
            //user_deleted webhook requires AppFeatures.WebhookFeature, AppFeatures.TestFeature but not requires all

            var (tenantId, data, predicate) = AsyncHelper.RunSync(() => InitializeTestCase(AppWebhookDefinitionNames.Users.Deleted,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"}
                }));

            AbpSession.TenantId = tenantId;
            _webhookPublisher.Publish(AppWebhookDefinitionNames.Users.Deleted, data);

            _backgroundJobManagerSubstitute.Received()
                .Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate(w)));

            _backgroundJobManagerSubstitute.ClearReceivedCalls();

            //DefaultThemeChanged webhook requires AppFeatures.WebhookFeature, AppFeatures.ThemeFeature and requires all
            var (tenantId2, data2, predicate2) = AsyncHelper.RunSync(() => InitializeTestCase(AppWebhookDefinitionNames.Theme.DefaultThemeChanged,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"},
                    {AppFeatures.ThemeFeature, "true"}
                }));

            AbpSession.TenantId = tenantId;
            _webhookPublisher.Publish(AppWebhookDefinitionNames.Theme.DefaultThemeChanged, data2, tenantId2);

            _backgroundJobManagerSubstitute.Received()
                .Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate2(w)));
        }

        [Fact]
        public void Should_Not_Send_Webhook_To_If_Tenant_Does_Not_Have_All_Features_When_Its_Required_All_Sync()
        {
            //DefaultThemeChanged webhook requires AppFeatures.WebhookFeature, AppFeatures.ThemeFeature and requires all
            var subscription = AsyncHelper.RunSync(() => CreateTenantAndSubscribeToWebhookAsync(AppWebhookDefinitionNames.Theme.DefaultThemeChanged,
               new Dictionary<string, string>
               {
                    {AppFeatures.WebhookFeature, "true"},
                    {AppFeatures.ThemeFeature, "true"}
               }));

            //remove one feature
            AsyncHelper.RunSync(() => AddOrReplaceFeatureToTenantAsync(subscription.TenantId.Value, AppFeatures.WebhookFeature, "false"));

            _webhookPublisher.Publish(AppWebhookDefinitionNames.Theme.DefaultThemeChanged, new { Name = "Musa", Surname = "Demir" }, subscription.TenantId);

            //should not try to send
            _backgroundJobManagerSubstitute.DidNotReceive().Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Any<WebhookSenderInput>());
        }

        [Fact]
        public void Should_Not_Send_Webhook_To_If_Current_Tenant_Does_Not_Have_All_Features_When_Its_Required_All_Sync()
        {
            //DefaultThemeChanged webhook requires AppFeatures.WebhookFeature, AppFeatures.ThemeFeature and requires all
            var subscription = AsyncHelper.RunSync(() => CreateTenantAndSubscribeToWebhookAsync(AppWebhookDefinitionNames.Theme.DefaultThemeChanged,
                new Dictionary<string, string>
                {
                    {AppFeatures.WebhookFeature, "true"},
                    {AppFeatures.ThemeFeature, "true"}
                }));

            AbpSession.TenantId = subscription.TenantId;
            //remove one feature
            AsyncHelper.RunSync(() => AddOrReplaceFeatureToTenantAsync(AbpSession.TenantId.Value, AppFeatures.WebhookFeature, "false"));

            _webhookPublisher.Publish(AppWebhookDefinitionNames.Theme.DefaultThemeChanged, new { Name = "Musa", Surname = "Demir" }, AbpSession.TenantId);

            //should not try to send
            _backgroundJobManagerSubstitute.DidNotReceive().Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Any<WebhookSenderInput>());
        }

        [Fact]
        public void Should_Send_Webhook_To_Host_If_Subscribed_Sync()
        {
            var subscription = new WebhookSubscription
            {
                TenantId = null,
                Secret = "secret",
                WebhookUri = "www.mywebhook.com",
                WebhookDefinitions = new List<string>() { AppWebhookDefinitionNames.Users.Created },
                Headers = new Dictionary<string, string>
                {
                    { "Key","Value"}
                }
            };

            var webhookSubscriptionManager = Resolve<IWebhookSubscriptionManager>();
            var webhooksConfiguration = Resolve<IWebhooksConfiguration>();

            webhookSubscriptionManager.AddOrUpdateSubscription(subscription);

            var data = new { Name = "Musa", Surname = "Demir" };

            Predicate<WebhookSenderInput> predicate = w =>
            {
                w.Secret.ShouldNotBeNullOrEmpty();
                w.Secret.ShouldStartWith("whs_");
                w.WebhookDefinition.ShouldContain(AppWebhookDefinitionNames.Users.Created);

                w.Headers.Count.ShouldBe(1);
                w.Headers.Single().Key.ShouldBe("Key");
                w.Headers.Single().Value.ShouldBe("Value");

                w.WebhookSubscriptionId.ShouldBe(subscription.Id);
                w.Data.ShouldBe(
                    webhooksConfiguration.JsonSerializerSettings != null
                        ? data.ToJsonString(webhooksConfiguration.JsonSerializerSettings)
                        : data.ToJsonString()
                );
                return true;
            };

            _webhookPublisher.Publish(AppWebhookDefinitionNames.Users.Created, data, null);

            _backgroundJobManagerSubstitute.Received()
               .Enqueue<WebhookSenderJob, WebhookSenderInput>(Arg.Is<WebhookSenderInput>(w => predicate(w)));
        }
        #endregion
    }
}
