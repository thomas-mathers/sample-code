// <copyright file="DealerController.cs" company="Idea Notion Development Inc">
// Copyright (c) Idea Notion Development Inc. All rights reserved.
// </copyright>

using System;
using System.Linq;
using System.Threading.Tasks;
using Bot.Core.Model.Entity;
using Bot.Core.Service;
using Bot.Core.Service.Email;
using Bot.Web.Middleware;
using Bot.Web.Model;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Bot.Web.Controllers
{
	[ApiExceptionFilter]
	[Produces("application/json")]
	[Route("api/dealer")]
	public class DealerController : Controller
	{
		private readonly IConfiguration _configuration;
		private readonly EmailService _emailService;
		private BotDbContext _context;

		public DealerController(BotDbContext context, IConfiguration configuration, EmailService emailService)
		{
			_context = context;
			_configuration = configuration;
			_emailService = emailService;
		}

		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		[HttpGet]
		[Route("{id}")]
		public async Task<dynamic> GetDealer(string id)
		{
			var dealer = await _context.Dealers
				.Include(t => t.Manufacturer)
				.Include(t => t.LuisConfig)
				.Include(t => t.QnaConfig)
				.Include(t => t.FacebookPage)
				.Include(t => t.UserDealers)
					.ThenInclude(s => s.User)
				.Include(t => t.UserDealers)
					.ThenInclude(s => s.Role)
				.FirstOrDefaultAsync(t => t.Id == id);

			if (dealer != null && dealer.UserDealers != null && dealer.UserDealers.Count != 0)
			{
				var existingUsers = dealer.UserDealers.Where(u => !u.User.Deleted).ToList();
				dealer.UserDealers = existingUsers;
			}

			return Ok(new DealerDetail(dealer, dealer.UserDealers.ToList()));
		}

		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		[HttpPut]
		[Route("{id}")]
		public async Task<dynamic> UpdateDealer(string id, [FromBody] DealerDetail json)
		{
			var dealer = await _context.Dealers
				.FirstOrDefaultAsync(t => t.Id == id);

			dealer.DealerName = json.DealerName;
			dealer.BotName = json.BotName;
			dealer.BotActive = json.BotActive;
			dealer.EnableHandover = json.EnableHandover;
			dealer.DirectChatKey = json.DirectChatKey;
			dealer.LastModifiedDate = DateTime.UtcNow;
			dealer.ManufacturerId = json.ManufacturerId;
			dealer.LuisConfigId = json.LuisConfigId;
			dealer.QnaConfigId = json.QnaConfigId;
			dealer.FacebookPageId = json.FacebookPageId;

			await _context.SaveChangesAsync();

			var newDealer = await _context.Dealers
			.Include(t => t.Manufacturer)
			.Include(t => t.LuisConfig)
			.Include(t => t.QnaConfig)
			.Include(t => t.FacebookPage)
			.FirstOrDefaultAsync(t => t.Id == id);

			return Ok(new DealerDetail(newDealer));
		}

		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		[HttpPost]
		[Route("create")]
		public async Task<dynamic> CreateDealer([FromBody] Core.DTOs.CarService.DealerModel dealerJson)
		{
			var newDealer = new Dealer(dealerJson.DealerName)
			{
				BotName = dealerJson.BotName,
				ManufacturerId = dealerJson.ManufacturerId,
				DirectChatKey = dealerJson.DirectChatKey,
				//FacebookPageId = dealerJson.FacebookPageId,
				LuisConfigId = (Guid)dealerJson.LuisConfigId,
				//QnaConfigId = dealerJson.QnaConfigId,
				BotActive = true,
				EnableHandover = false,
				LastModifiedDate = DateTime.UtcNow,
				CreatedDate = DateTime.UtcNow,
				AutoOnOffSchedule = false,
				AutoMLModelName = dealerJson.AutoMLModelName,
			};

			var dealer = _context.Dealers.FirstOrDefault(t => t.Id == newDealer.Id);
			if (dealer != null)
			{
				throw new InvalidOperationException("Dealer ID Taken");
			}

			try
			{
				_context.Dealers.Add(newDealer);
				await _context.SaveChangesAsync();

			}
			catch (Exception e) {
				var a = 'b';
			}


			var d = await _context.Dealers
				.Include(t => t.Manufacturer)
				.Include(t => t.LuisConfig)
				.Include(t => t.QnaConfig)
				.Include(t => t.FacebookPage)
				.FirstOrDefaultAsync(t => t.Id == newDealer.Id);

			return Ok(new DealerDetail(d));
		}

		[HttpGet]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		public async Task<dynamic> Get(
			[FromQuery] string dealername = null,
			[FromQuery] string manufacturer = null,
			[FromQuery] string sortBy = null,
			[FromQuery] string sortByDir = null,
			[FromQuery] int pageIndex = 0,
			[FromQuery] int pageSize = 1000)
		{
			if (!ModelState.IsValid)
			{
				return BadRequest(ModelState);
			}

			var dealers = _context.Dealers.Include(t => t.FacebookPage).AsQueryable();

			if (!string.IsNullOrEmpty(dealername))
			{
				dealers = dealers.Where(t => t.DealerName.Contains(dealername));
			}

			if (!string.IsNullOrEmpty(manufacturer))
			{
				dealers = dealers.Where(t => t.ManufacturerId == manufacturer);
			}

			switch (sortBy)
			{
				case "DealerName":
					dealers = sortByDir == "desc" ? dealers.OrderByDescending(t => t.DealerName) : dealers.OrderBy(t => t.DealerName);
					break;
				case "FacebookPageId":
					dealers = sortByDir == "desc" ? dealers.OrderByDescending(t => (t.FacebookPage == null) ? string.Empty : t.FacebookPage.PageId) : dealers.OrderBy(t => (t.FacebookPage == null) ? string.Empty : t.FacebookPage.PageId);
					break;
				case "CreatedDate":
					dealers = sortByDir == "desc" ? dealers.OrderByDescending(t => t.CreatedDate) : dealers.OrderBy(t => t.CreatedDate);
					break;
				case "ModifiedDate":
					dealers = sortByDir == "desc" ? dealers.OrderByDescending(t => t.LastModifiedDate) : dealers.OrderBy(t => t.LastModifiedDate);
					break;
				default:
					dealers = dealers.OrderByDescending(t => t.CreatedDate);
					break;
			}

			var count = dealers.Count();
			dealers = dealers.Skip(pageIndex * pageSize).Take(pageSize);

			var data = await dealers.Select(t => new DealerSummary(t)).ToListAsync();

			return Ok(new PaginatedResponse<DealerSummary>(data, pageSize, pageIndex, count));
		}

		[HttpGet]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		[Route("luisConfigs")]
		public async Task<dynamic> GetLuisConfig()
		{
			var luisConfigs = await _context.LuisConfigs.ToListAsync();

			return Ok(luisConfigs);
		}
	}
}
