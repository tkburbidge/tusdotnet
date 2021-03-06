﻿using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Owin.Testing;
using NSubstitute;
using Owin;
using Shouldly;
using tusdotnet.Interfaces;
using tusdotnet.Models;
using tusdotnet.test.Extensions;
using Xunit;

namespace tusdotnet.test.Tests
{
	public class OptionsTests
	{
		private static ITusConfiguration _mockTusConfiguration;

		public OptionsTests()
		{
			_mockTusConfiguration = new DefaultTusConfiguration
			{
				Store = Substitute.For<ITusStore, ITusCreationStore>(),
				UrlPath = "/files"
			};
		}

		[Fact]
		public async Task Ignores_Request_If_Url_Does_Not_Match()
		{
			var callForwarded = false;
			using (var server = TestServer.Create(app =>
			{
				app.UseTus(request => _mockTusConfiguration);

				app.Use((ctx, next) =>
				{
					callForwarded = true;
					return Task.FromResult(true);
				});

			}))
			{
				await server
						.CreateRequest("/files")
						.AddTusResumableHeader()
						.SendAsync("OPTIONS");

				callForwarded.ShouldBeFalse();

				await server
					.CreateRequest("/otherfiles")
					.AddTusResumableHeader()
					.SendAsync("OPTIONS");

				callForwarded.ShouldBeTrue();

				callForwarded = false;

				await server
						.CreateRequest("/files/testfile")
						.AddTusResumableHeader()
						.SendAsync("OPTIONS");

				callForwarded.ShouldBeTrue();
			}
		}

		[Fact]
		public async Task Returns_204_NoContent_On_Success()
		{
			using (var server = TestServer.Create(app =>
			{
				app.UseTus(request => _mockTusConfiguration);
			}))
			{
				var response = await server.CreateRequest("/files").SendAsync("OPTIONS");
				response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
			}
		}

		[Fact]
		public async Task Response_Contains_The_Correct_Headers_On_Success()
		{

			using (var server = TestServer.Create(app =>
			{
				app.UseTus(request => _mockTusConfiguration);
			}))
			{
				var response = await server.CreateRequest("/files").SendAsync("OPTIONS");

				response.Headers.Contains("Tus-Resumable").ShouldBeTrue();
				var tusResumable = response.Headers.GetValues("Tus-Resumable").ToList();
				tusResumable.Count.ShouldBe(1);
				tusResumable.First().ShouldBe("1.0.0");

				response.Headers.Contains("Tus-Version").ShouldBeTrue();
				var tusVersion = response.Headers.GetValues("Tus-Version").ToList();
				tusVersion.Count.ShouldBe(1);
				tusVersion.First().ShouldBe("1.0.0");

				response.Headers.Contains("Tus-Extension").ShouldBeTrue();
				var tusExtension = response.Headers.GetValues("Tus-Extension").ToList();
				tusExtension.Count.ShouldBe(1);
				tusExtension.First().ShouldBe("creation");

			}

			// Test again but with a store that does not implement ITusCreationStore.
			using (var server = TestServer.Create(app =>
			{
				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = Substitute.For<ITusStore>(),
					UrlPath = "/files"
				});
			}))
			{
				var response = await server.CreateRequest("/files").SendAsync("OPTIONS");

				response.Headers.Contains("Tus-Resumable").ShouldBeTrue();
				var tusResumable = response.Headers.GetValues("Tus-Resumable").ToList();
				tusResumable.Count.ShouldBe(1);
				tusResumable.First().ShouldBe("1.0.0");

				response.Headers.Contains("Tus-Version").ShouldBeTrue();
				var tusVersion = response.Headers.GetValues("Tus-Version").ToList();
				tusVersion.Count.ShouldBe(1);
				tusVersion.First().ShouldBe("1.0.0");

				// Store does not implement any extensions.
				response.Headers.Contains("Tus-Extension").ShouldBeFalse();

			}
		}
	}
}
