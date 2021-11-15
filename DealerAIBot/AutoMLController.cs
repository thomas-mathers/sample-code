// <copyright file="AutoMLController.cs" company="Idea Notion Development Inc">
// Copyright (c) Idea Notion Development Inc. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Bot.Core.DTOs;
using Bot.Core.DTOs.AutoML;
using Bot.Core.Exceptions;
using Bot.Core.Model.Entity;
using Bot.Core.Service;
using Bot.Core.Service.AutoML;
using Bot.Web.DTOs;
using Bot.Web.Middleware;
using Bot.Web.Model;
using CsvHelper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Storage.Blob;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Bot.Web.Controllers
{
	[ApiExceptionFilter]
	[Produces("application/json")]
	[Route("api/automl")]
	public class AutoMLController : Controller
	{
		private readonly AutoMLAuthoringService autoMLAuthoringService;
		private readonly ProjectSettings projectSettings;
		private readonly HttpClient httpClient;
		private readonly BotDbContext botDbContext;
		private readonly BatchTestService batchTestService;
		private readonly IConfiguration configuration;
		private readonly int requestsPerSecond;
		private readonly int delay;
		private readonly IDistributedCache cache;

		public AutoMLController(IConfiguration configuration, HttpClient httpClient, ProjectSettings projectSettings, AutoMLAuthoringService autoMLAuthoringService, BotDbContext botDbContext, BatchTestService batchTestService, IDistributedCache cache)
		{
			this.configuration = configuration;
			this.autoMLAuthoringService = autoMLAuthoringService;
			this.botDbContext = botDbContext;
			this.projectSettings = projectSettings;
			this.httpClient = httpClient;
			this.batchTestService = batchTestService;
			this.cache = cache;

			requestsPerSecond = int.Parse(configuration["AutoMLRequestsPerSecond"]);
			delay = int.Parse(configuration["AutoMLRequestDelay"]);
		}

		[HttpPost("datasets")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<ActionResult<Operation>> CreateDatasetAsync([FromBody] CreateDatasetBody body)
		{
			try
			{
				return Ok(await autoMLAuthoringService.CreateDatasetAsync(body.Name, body.Utterances));
			}
			catch (HttpStatusCodeException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
			{
				return Conflict($"Dataset with name {body.Name} already exists");
			}
			catch (HttpStatusCodeException ex)
			{
				return StatusCode((int)ex.StatusCode);
			}
		}

		[HttpGet("datasets")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<ActionResult<List<AutoMLDataset>>> GetDatasetsAsync()
		{
			try
			{
				var datasets = await autoMLAuthoringService.GetDatasetsAsync();
				var datasetDTOs = new List<AutoMLDataset>();

				foreach (var dataset in datasets)
				{
					var dto = new AutoMLDataset(dataset);

					var operations = await autoMLAuthoringService.GetOperationsAsync($"worksOn={dataset.Name}");

					if (operations.Count == 0)
					{
						dto.LastOperationStatus = OperationStatus.Idle;
					}
					else
					{
						var latestOperation = operations[0];

						for (var i = 1; i < operations.Count; i++)
						{
							if (operations[i].Metadata.Value<string>("updateTime").CompareTo(latestOperation.Metadata.Value<string>("updateTime")) > 0)
							{
								latestOperation = operations[i];
							}
						}

						dto.LastOperationStatus = latestOperation.Done ? OperationStatus.Done : OperationStatus.Busy;
					}

					datasetDTOs.Add(dto);
				}

				return Ok(datasetDTOs);
			}
			catch (HttpStatusCodeException ex)
			{
				return StatusCode((int)ex.StatusCode);
			}
		}

		[HttpGet("datasets/{displayName}/utterances")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<ActionResult<Utterance[]>> GetUtterancesAsync(string displayName)
		{
			try
			{
				return Ok(await autoMLAuthoringService.GetUtterancesAsync(displayName));
			}
			catch (HttpStatusCodeException ex)
			{
				return StatusCode((int)ex.StatusCode);
			}
		}

		[HttpPost("models")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<ActionResult<Operation>> CreateModelAsync([FromBody] Core.DTOs.AutoML.Model body)
		{
			try
			{
				return Ok(await autoMLAuthoringService.CreateModelAsync(body));
			}
			catch (HttpStatusCodeException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
			{
				return Conflict($"Model with name {body.Name} already exists");
			}
			catch (HttpStatusCodeException ex)
			{
				return StatusCode((int)ex.StatusCode);
			}
		}

		[HttpPost("models:deploy")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<ActionResult<Operation>> DeployModelAsync([FromBody] Core.DTOs.AutoML.Model model)
		{
			try
			{
				return Ok(await autoMLAuthoringService.DeployModelAsync(model.Name));
			}
			catch (HttpStatusCodeException ex)
			{
				return StatusCode((int)ex.StatusCode);
			}
		}

		[HttpPost("models:activate")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<ActionResult> ActivateModelAsync([FromBody] Core.DTOs.AutoML.Model model)
		{
			foreach (var dealer in botDbContext.Dealers)
			{
				dealer.AutoMLModelName = model.Name;
			}

			await botDbContext.SaveChangesAsync();

			return Ok();
		}

		[HttpPost("models:batch-predict")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<ActionResult<IList<BatchTestResult>>> BatchTestAsync([FromBody] DTOs.AutoMLBatchTest batchTest)
		{
			var utterances = await batchTestService.GetBatchTestUtterancesAsync(batchTest.TestName);
			var predictionService = new AutoMLPredictionService(httpClient, projectSettings, batchTest.ModelName, cache);
			var predictions = await predictionService.BatchPredictAsync(utterances, requestsPerSecond, delay);
			return Ok(predictions);
		}

		[HttpPost("dealermodel:activate/{dealerId}")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<ActionResult> ActivateDealerModelAsync(string dealerId, [FromBody] Core.DTOs.AutoML.Model model)
		{
			var dealer = botDbContext.Dealers.First(d => d.Id.Equals(dealerId));
			dealer.AutoMLModelName = model.Name;

			await botDbContext.SaveChangesAsync();

			return Ok();
		}

		[HttpGet("models")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<ActionResult<List<Core.DTOs.AutoML.Model>>> GetModelsAsync()
		{
			try
			{
				return Ok(await autoMLAuthoringService.GetModelsAsync());
			}
			catch (HttpStatusCodeException ex)
			{
				return StatusCode((int)ex.StatusCode);
			}
		}

		[HttpGet("operations")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<ActionResult<List<Operation>>> GetOperationsAsync([FromQuery] string filter)
		{
			try
			{
				return Ok(await autoMLAuthoringService.GetOperationsAsync(filter));
			}
			catch (HttpStatusCodeException ex)
			{
				return StatusCode((int)ex.StatusCode);
			}
		}

		[HttpGet("dealers")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<ActionResult<List<Dealer>>> GetDealersAsync()
		{
			try
			{
				var dealers = await botDbContext.Dealers.Select(t => new DealerSummary(t)).ToListAsync();

				return Ok(dealers);
			}
			catch (HttpStatusCodeException ex)
			{
				return StatusCode((int)ex.StatusCode);
			}
		}
	}
}
