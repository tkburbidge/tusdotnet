﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Owin.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Owin;
using Shouldly;
using tusdotnet.Interfaces;
using tusdotnet.Models;
using tusdotnet.Stores;
using tusdotnet.test.Extensions;
using Xunit;

namespace tusdotnet.test.Tests
{
	public class PatchTests
	{
		[Fact]
		public async Task Ignores_Request_If_Url_Does_Not_Match()
		{
			var callForwarded = false;
			using (var server = TestServer.Create(app =>
			{
				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = new TusDiskStore(@"C:\temp"),
					UrlPath = "/files"
				});

				app.Use((ctx, next) =>
				{
					callForwarded = true;
					return Task.FromResult(true);
				});

			}))
			{
				await server
					.CreateRequest("/files")
					.AddHeader("Tus-Resumable", "1.0.0")
					.SendAsync("PATCH");

				callForwarded.ShouldBeTrue();

				callForwarded = false;

				await server
					.CreateRequest("/files/testfile")
					.AddHeader("Tus-Resumable", "1.0.0")
					.SendAsync("PATCH");

				callForwarded.ShouldBeFalse();

				await server
					.CreateRequest("/otherfiles/testfile")
					.AddHeader("Tus-Resumable", "1.0.0")
					.SendAsync("PATCH");

				callForwarded.ShouldBeTrue();
			}
		}

		[Fact]
		public async Task Returns_404_Not_Found_If_File_Was_Not_Found()
		{

			using (var server = TestServer.Create(app =>
			{
				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = Substitute.For<ITusStore>(),
					UrlPath = "/files"
				});
			}))
			{
				var response = await server
					.CreateRequest("/files/testfile")
					.And(AddBody)
					.AddHeader("Upload-Offset", "0")
					.AddTusResumableHeader()
					.SendAsync("PATCH");

				response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
				response.ShouldContainHeader("Cache-Control", "no-store");
				response.ShouldContainTusResumableHeader();
			}
		}

		[Theory]
		[InlineData("text/plain")]
		[InlineData(null)]
		[InlineData("application/octet-stream")]
		[InlineData("application/json")]
		public async Task Returns_400_Bad_Request_If_An_Incorrect_Content_Type_Is_Provided(string contentType)
		{
			using (var server = TestServer.Create(app =>
			{
				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = Substitute.For<ITusStore>(),
					UrlPath = "/files"
				});
			}))
			{
				var requestBuilder = server
					.CreateRequest("/files/testfile")
					.AddHeader("Upload-Offset", "0")
					.AddTusResumableHeader();

				if (contentType != null)
				{
					requestBuilder = requestBuilder.And(message => AddBody(message, contentType));
				}

				var response = await requestBuilder.SendAsync("PATCH");

				await response.ShouldBeErrorResponse(HttpStatusCode.BadRequest,
					$"Content-Type {contentType} is invalid. Must be application/offset+octet-stream");
				response.ShouldContainTusResumableHeader();
			}
		}

		[Fact]
		public async Task Returns_400_Bad_Request_For_Missing_Upload_Offset_Header()
		{
			using (var server = TestServer.Create(app =>
			{
				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = Substitute.For<ITusStore>(),
					UrlPath = "/files"
				});
			}))
			{

				var response = await server
					.CreateRequest("/files/testfile")
					.And(AddBody)
					.AddTusResumableHeader()
					.SendAsync("patch");

				await response.ShouldBeErrorResponse(HttpStatusCode.BadRequest, "Missing Upload-Offset header");
			}
		}

		[Theory]
		[InlineData("uploadoffset", "")]
		[InlineData("1.0.1", "")]
		[InlineData("0.2", "")]
		[InlineData("-100", "Header Upload-Offset must be a positive number")]
		public async Task Returns_400_Bad_Request_For_Invalid_Upload_Offset_Header(string uploadOffset,
			string expectedServerErrorMessage)
		{
			using (var server = TestServer.Create(app =>
			{
				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = Substitute.For<ITusStore>(),
					UrlPath = "/files"
				});
			}))
			{

				var response = await server
					.CreateRequest("/files/testfile")
					.And(AddBody)
					.AddHeader("Upload-Offset", uploadOffset)
					.AddTusResumableHeader()
					.SendAsync("patch");

				await
					response.ShouldBeErrorResponse(HttpStatusCode.BadRequest,
						string.IsNullOrEmpty(expectedServerErrorMessage)
							? "Could not parse Upload-Offset header"
							: expectedServerErrorMessage);
			}
		}

		[Theory]
		[InlineData(5)]
		[InlineData(100)]
		public async Task Returns_409_Conflict_If_Upload_Offset_Does_Not_Match_File_Offset(int offset)
		{
			using (var server = TestServer.Create(app =>
			{
				var store = Substitute.For<ITusStore>();
				store.FileExistAsync("testfile", Arg.Any<CancellationToken>()).Returns(true);
				store.GetUploadOffsetAsync("testfile", Arg.Any<CancellationToken>()).Returns(10);

				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = store,
					UrlPath = "/files"
				});
			}))
			{

				var response = await server
					.CreateRequest("/files/testfile")
					.And(AddBody)
					.AddTusResumableHeader()
					.AddHeader("Upload-Offset", offset.ToString())
					.SendAsync("PATCH");

				await response.ShouldBeErrorResponse(HttpStatusCode.Conflict,
					$"Offset does not match file. File offset: 10. Request offset: {offset}");
			}
		}

		[Fact]
		public async Task Returns_400_Bad_Request_If_Upload_Is_Already_Complete()
		{
			using (var server = TestServer.Create(app =>
			{
				var store = Substitute.For<ITusStore>();
				store.FileExistAsync("testfile", Arg.Any<CancellationToken>()).Returns(true);
				store.GetUploadOffsetAsync("testfile", Arg.Any<CancellationToken>()).Returns(10);
				store.GetUploadLengthAsync("testfile", Arg.Any<CancellationToken>()).Returns(10);

				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = store,
					UrlPath = "/files"
				});
			}))
			{

				var response = await server
					.CreateRequest("/files/testfile")
					.And(AddBody)
					.AddTusResumableHeader()
					.AddHeader("Upload-Offset", "10")
					.SendAsync("PATCH");

				await response.ShouldBeErrorResponse(HttpStatusCode.BadRequest, "Upload is already complete.");
			}
		}

		[Fact]
		public async Task Returns_204_No_Content_On_Success()
		{
			using (var server = TestServer.Create(app =>
			{
				var store = Substitute.For<ITusStore>();
				store.FileExistAsync("testfile", Arg.Any<CancellationToken>()).Returns(true);
				store.GetUploadOffsetAsync("testfile", Arg.Any<CancellationToken>()).Returns(5);
				store.GetUploadLengthAsync("testfile", Arg.Any<CancellationToken>()).Returns(10);
				store.AppendDataAsync("testfile", Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(5);

				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = store,
					UrlPath = "/files"
				});
			}))
			{

				var response = await server
					.CreateRequest("/files/testfile")
					.And(AddBody)
					.AddTusResumableHeader()
					.AddHeader("Upload-Offset", "5")
					.SendAsync("PATCH");

				response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
			}
		}

		[Fact]
		public async Task Response_Contains_The_Correct_Headers_On_Success()
		{
			using (var server = TestServer.Create(app =>
			{
				var store = Substitute.For<ITusStore>();
				store.FileExistAsync("testfile", Arg.Any<CancellationToken>()).Returns(true);
				store.GetUploadOffsetAsync("testfile", Arg.Any<CancellationToken>()).Returns(5);
				store.GetUploadLengthAsync("testfile", Arg.Any<CancellationToken>()).Returns(10);
				store.AppendDataAsync("testfile", Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(5);

				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = store,
					UrlPath = "/files"
				});
			}))
			{
				var response = await server
					.CreateRequest("/files/testfile")
					.And(AddBody)
					.AddTusResumableHeader()
					.AddHeader("Upload-Offset", "5")
					.SendAsync("PATCH");

				response.ShouldContainTusResumableHeader();
				response.ShouldContainHeader("Upload-Offset", "10");
			}
		}

		[Fact]
		public async Task Returns_Store_Exceptions_As_400_Bad_Request()
		{
			// TODO: This test does not work properly using the OWIN TestServer.
			// It will always throw an exception instead of returning the proper error message to the client.
			using (var server = TestServer.Create(app =>
			{
				var store = Substitute.For<ITusStore>();
				store.FileExistAsync("testfile", Arg.Any<CancellationToken>()).Returns(true);
				store.GetUploadOffsetAsync("testfile", Arg.Any<CancellationToken>()).Returns(5);
				store.GetUploadLengthAsync("testfile", Arg.Any<CancellationToken>()).Returns(10);
				store.AppendDataAsync("testfile", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
					.Throws(new TusStoreException("Test exception"));

				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = store,
					UrlPath = "/files"
				});
			}))
			{
				var requestBuilder = server
					.CreateRequest("/files/testfile")
					.And(AddBody)
					.AddTusResumableHeader()
					.AddHeader("Upload-Offset", "5");

				try
				{
					await requestBuilder.SendAsync("PATCH");
					true.ShouldBeFalse("Exception was not thrown.");
				}
				catch (HttpRequestException)
				{
					// Left blank.
				}
				catch (TusStoreException)
				{
					// Left blank.
				}
			}
		}

		[Fact]
		public async Task Handles_Abrupt_Disconnects_Gracefully()
		{
			using (var server = TestServer.Create(app =>
			{
				var store = Substitute.For<ITusStore>();
				store.FileExistAsync("testfile", Arg.Any<CancellationToken>()).Returns(true);
				store.GetUploadOffsetAsync("testfile", Arg.Any<CancellationToken>()).Returns(5);
				store.GetUploadLengthAsync("testfile", Arg.Any<CancellationToken>()).Returns(10);
				// IOException with an inner HttpListenerException is a client disconnect.
				store.AppendDataAsync("testfile", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
					.Throws(new IOException("Test exception", new HttpListenerException()));

				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = store,
					UrlPath = "/files"
				});
			}))
			{
				var response = await server
					.CreateRequest("/files/testfile")
					.And(AddBody)
					.AddTusResumableHeader()
					.AddHeader("Upload-Offset", "5")
					.SendAsync("PATCH");

				response.StatusCode.ShouldBe(HttpStatusCode.OK);
				var body = await response.Content.ReadAsStreamAsync();
				body.Length.ShouldBe(0);
			}

			// IOException without an inner HttpListenerException is not a disconnect.
			using (var server = TestServer.Create(app =>
			{
				var store = Substitute.For<ITusStore>();
				store.FileExistAsync("testfile", Arg.Any<CancellationToken>()).Returns(true);
				store.GetUploadOffsetAsync("testfile", Arg.Any<CancellationToken>()).Returns(5);
				store.GetUploadLengthAsync("testfile", Arg.Any<CancellationToken>()).Returns(10);
				store.AppendDataAsync("testfile", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
					.Throws(new IOException("Test exception"));

				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = store,
					UrlPath = "/files"
				});
			}))
			{
				// ReSharper disable once AccessToDisposedClosure
				await Should.ThrowAsync<IOException>(async () => await server
					.CreateRequest("/files/testfile")
					.And(AddBody)
					.AddTusResumableHeader()
					.AddHeader("Upload-Offset", "5")
					.SendAsync("PATCH"));
			}
		}

		[Fact]
		public async Task Returns_409_Conflict_If_Multiple_Requests_Try_To_Patch_The_Same_File()
		{
			using (var server = TestServer.Create(app =>
			{
				var random = new Random();
				var offset = 5;
				var store = Substitute.For<ITusStore>();
				store.FileExistAsync("testfile", Arg.Any<CancellationToken>()).Returns(true);
				store
					.GetUploadOffsetAsync("testfile", Arg.Any<CancellationToken>())
					.Returns(offset);
				store.GetUploadLengthAsync("testfile", Arg.Any<CancellationToken>()).Returns(10);
				store
					.AppendDataAsync("testfile", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
					.Returns(info =>
					{
						// Emulate some latency in the request.
						Thread.Sleep(random.Next(100, 301));
						offset += 3;
						return 3;
					});

				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = store,
					UrlPath = "/files"
				});

			}))
			{
				// Duplicated code due to: 
				// "System.InvalidOperationException: The request message was already sent. Cannot send the same request message multiple times."
				var task1 = server
					.CreateRequest("/files/testfile")
					.And(message =>
					{
						message.Content = new StreamContent(new MemoryStream(new byte[3]));
						message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
					})
					.AddHeader("Upload-Offset", "5")
					.AddTusResumableHeader()
					.SendAsync("PATCH");
				var task2 = server
					.CreateRequest("/files/testfile")
					.And(message =>
					{
						message.Content = new StreamContent(new MemoryStream(new byte[3]));
						message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
					})
					.AddHeader("Upload-Offset", "5")
					.AddTusResumableHeader()
					.SendAsync("PATCH");

				await Task.WhenAll(task1, task2);

				if (task1.Result.StatusCode == HttpStatusCode.NoContent)
				{
					task1.Result.StatusCode.ShouldBe(HttpStatusCode.NoContent);
					task2.Result.StatusCode.ShouldBe(HttpStatusCode.Conflict);
				}
				else
				{
					task1.Result.StatusCode.ShouldBe(HttpStatusCode.Conflict);
					task2.Result.StatusCode.ShouldBe(HttpStatusCode.NoContent);
				}
			}
		}

		[Fact]
		public async Task Runs_OnUploadComplete_When_Upload_Is_Complete()
		{
			var onUploadCompleteCallCounts = new Dictionary<string, int>();
			var firstOffset = 3;
			var secondOffset = 2;

			using (var server = TestServer.Create(app =>
			{
				var store = Substitute.For<ITusStore>();
				store.FileExistAsync("file1", Arg.Any<CancellationToken>()).Returns(true);
				store.GetUploadLengthAsync("file1", Arg.Any<CancellationToken>()).Returns(6);
				store.GetUploadOffsetAsync("file1", Arg.Any<CancellationToken>()).Returns(info => firstOffset);
				store.AppendDataAsync("file1", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
					.Returns(3)
					.AndDoes(info => firstOffset += 3);

				store.FileExistAsync("file2", Arg.Any<CancellationToken>()).Returns(true);
				store.GetUploadLengthAsync("file2", Arg.Any<CancellationToken>()).Returns(6);
				store.GetUploadOffsetAsync("file2", Arg.Any<CancellationToken>()).Returns(info => secondOffset);
				store.AppendDataAsync("file2", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
					.Returns(3)
					.AndDoes(info => secondOffset += 3);

				app.UseTus(request => new DefaultTusConfiguration
				{
					Store = store,
					UrlPath = "/files",
					OnUploadCompleteAsync = (fileId, cbStore, cancellationToken) =>
					{
						// Check that the store provided is the same as the one in the configuration.
						cbStore.ShouldBeSameAs(store);
						cancellationToken.ShouldNotBe(default(CancellationToken));

						var count = 0;
						if (onUploadCompleteCallCounts.ContainsKey(fileId))
						{
							count = onUploadCompleteCallCounts[fileId];
						}

						count++;
						onUploadCompleteCallCounts[fileId] = count;
						return Task.FromResult(true);
					}
				});
			}))
			{
				var response1 = await server
					.CreateRequest("/files/file1")
					.And(AddBody)
					.AddHeader("Upload-Offset", "3")
					.AddTusResumableHeader()
					.SendAsync("PATCH");

				response1.StatusCode.ShouldBe(HttpStatusCode.NoContent);

				var response2 = await server
					.CreateRequest("/files/file2")
					.And(AddBody)
					.AddHeader("Upload-Offset", "2")
					.AddTusResumableHeader()
					.SendAsync("PATCH");

				response2.StatusCode.ShouldBe(HttpStatusCode.NoContent);

				// File is already complete, make sure it does not run OnUploadComplete twice.
				response1 = await server
					.CreateRequest("/files/file1")
					.And(AddBody)
					.AddHeader("Upload-Offset", "6")
					.AddTusResumableHeader()
					.SendAsync("PATCH");

				response1.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

				onUploadCompleteCallCounts.Keys.Count.ShouldBe(1);
				onUploadCompleteCallCounts.ContainsKey("file1").ShouldBeTrue();
				onUploadCompleteCallCounts["file1"].ShouldBe(1);
			}
		}

		private static void AddBody(HttpRequestMessage message)
		{
			AddBody(message, "application/offset+octet-stream");
		}

		private static void AddBody(HttpRequestMessage message, string contentType)
		{
			message.Content = new ByteArrayContent(new byte[] { 0, 0, 0 });
			message.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
		}
	}
}
