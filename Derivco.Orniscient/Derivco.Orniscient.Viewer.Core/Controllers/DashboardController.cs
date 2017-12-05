﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Derivco.Orniscient.Proxy.Core.Grains;
using Derivco.Orniscient.Proxy.Core.Grains.Filters;
using Derivco.Orniscient.Proxy.Core.Grains.Models;
using Derivco.Orniscient.Viewer.Core.Clients;
using Derivco.Orniscient.Viewer.Core.Hubs;
using Derivco.Orniscient.Viewer.Core.Models.Dashboard;
using Derivco.Orniscient.Viewer.Core.Observers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols;
using ConnectionInfo = Derivco.Orniscient.Viewer.Core.Models.Connection.ConnectionInfo;

namespace Derivco.Orniscient.Viewer.Core.Controllers
{
    public class DashboardController : Controller
    {
        private const string GrainSessionIdTypeName = "GrainSessionId";
        private const string PortTypeName = "Port";
        private const string AddressTypeName = "Address";
        private static bool _allowMethodsInvocation;
        private readonly IConfiguration _configuration;

        public DashboardController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string GrainSessionId
        {
            get
            {
                return HttpContext.User.Claims.First(x => x.Type == GrainSessionIdTypeName).Value;
            }
        }

        // GET: Dashboard
        public async Task<ViewResult> Index(ConnectionInfo connection)
        {
            try
            {
                await TryCleanupConnection(connection);

                if(!HttpContext.User.Identity.IsAuthenticated)
                {
                    var grainSessionIdKey = GrainClientMultiton.RegisterClient(connection.Address, connection.Port);
                    var claimsPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
                        new List<Claim>
                        {
                            new Claim(AddressTypeName,connection.Address),
                            new Claim(PortTypeName, connection.Port.ToString()),
                            new Claim(GrainSessionIdTypeName, grainSessionIdKey)
                        }));
                    await HttpContext.SignInAsync(claimsPrincipal);
                }

                _allowMethodsInvocation = AllowMethodsInvocation();
                ViewBag.AllowMethodsInvocation = _allowMethodsInvocation;
                return View();
            }
            catch (Exception ex)
            {
                return View("InitError");
            }
        }

        private async Task TryCleanupConnection(ConnectionInfo connection)
        {
            if (HttpContext.User.Identity.IsAuthenticated)
            {
                var client = GrainClientMultiton.GetClient(GrainSessionId);
                var gateway = client.Configuration.Gateways.First();
                if (gateway.Address.ToString() != connection.Address ||
                    gateway.Port != connection.Port)
                {
                    await CleanupClient();
                    await HttpContext.SignOutAsync();
                }
            }
        }

        public async Task<ActionResult> Disconnect()
        {
            await CleanupClient();
            await HttpContext.SignOutAsync();
            return RedirectToAction("Index", "Connection");
        }

        private async Task CleanupClient()
        {
            await OrniscientObserver.Instance.UnregisterGrainClient(GrainSessionId);
            GrainClientMultiton.RemoveClient(GrainSessionId);
        }

        public async Task<ActionResult> GetDashboardInfo()
		{
            var clusterClient = await GrainClientMultiton.GetAndConnectClient(GrainSessionId);
            var dashboardCollectorGrain = clusterClient.GetGrain<IDashboardCollectorGrain>(Guid.Empty);

		    var types = await dashboardCollectorGrain.GetGrainTypes();

			var dashboardInfo = new DashboardInfo
			{
				Silos = await dashboardCollectorGrain.GetSilos(),
				AvailableTypes = types
			};

			return Json(dashboardInfo);
		}

		public async Task<ActionResult> GetFilters(GetFiltersRequest filtersRequest)
		{
			if (filtersRequest?.Types == null)
				return null;

		    var clusterClient = await GrainClientMultiton.GetAndConnectClient(GrainSessionId);
            var filterGrain = clusterClient.GetGrain<IFilterGrain>(Guid.Empty);
			var filters = await filterGrain.GetGroupedFilterValues(filtersRequest.Types);
			return Json(filters);
		}

		[HttpPost]
		public async Task<ActionResult> GetGrainInfo(GetGrainInfoRequest grainInfoRequest)
		{
		    var clusterClient = await GrainClientMultiton.GetAndConnectClient(GrainSessionId);
            var typeFilterGrain = clusterClient.GetGrain<IFilterGrain>(Guid.Empty);
			var filters = await typeFilterGrain.GetFilters(grainInfoRequest.GrainType, grainInfoRequest.GrainId);
			return Json(filters);
		}

		[HttpPost]
		public async Task SetSummaryViewLimit(int summaryViewLimit)
		{
		    var clusterClient = await GrainClientMultiton.GetAndConnectClient(GrainSessionId);
            var dashboardInstanceGrain = clusterClient.GetGrain<IDashboardInstanceGrain>(0);
			await dashboardInstanceGrain.SetSummaryViewLimit(summaryViewLimit);
		}

		[HttpPost]
		public async Task<ActionResult> GetInfoForGrainType(string type)
		{
		    var clusterClient = await GrainClientMultiton.GetAndConnectClient(GrainSessionId);
            var dashboardCollectorGrain = clusterClient.GetGrain<IDashboardCollectorGrain>(Guid.Empty);
			var ids = await dashboardCollectorGrain.GetGrainIdsForType(type);

            var grainInfoGrain = clusterClient.GetGrain<IMethodInvocationGrain>(type);
			var methods = new List<GrainMethod>();
			if (_allowMethodsInvocation)
			{
				methods = await grainInfoGrain.GetAvailableMethods();
			}
			var keyType = await grainInfoGrain.GetGrainKeyType();

			return Json(new {Methods = methods, Ids = ids, KeyType = keyType});
		}

		[HttpGet]
		public async Task<ActionResult> GetAllGrainTypes()
		{
		    var clusterClient = await GrainClientMultiton.GetAndConnectClient(GrainSessionId);
            var dashboardCollectorGrain = clusterClient.GetGrain<IDashboardCollectorGrain>(Guid.Empty);
			var types = await dashboardCollectorGrain.GetGrainTypes();
			return Json(types);
		}

		[HttpPost]
		public async Task<ActionResult> GetGrainKeyFromType(string type)
		{
		    var clusterClient = await GrainClientMultiton.GetAndConnectClient(GrainSessionId);
            var grainInfoGrain = clusterClient.GetGrain<IMethodInvocationGrain>(type);
			var grainKeyType = await grainInfoGrain.GetGrainKeyType();
			return Json(grainKeyType);
		}

		[HttpPost]
		public async Task<ActionResult> InvokeGrainMethod(string type, string id, string methodId, string parametersJson,
			bool invokeOnNewGrain = false)
		{
			if (_allowMethodsInvocation)
			{
				try
				{
				    var clusterClient = await GrainClientMultiton.GetAndConnectClient(GrainSessionId);
                    var methodGrain = clusterClient.GetGrain<IMethodInvocationGrain>(type);
					var methodReturnData = await methodGrain.InvokeGrainMethod(id, methodId, parametersJson);
					return Json(methodReturnData);

				}
				catch (Exception ex)
				{
					return new StatusCodeResult((int)HttpStatusCode.BadRequest);
				}
			}
			return new StatusCodeResult((int)HttpStatusCode.Forbidden);
		}

        private bool AllowMethodsInvocation()
		{
			bool allowMethodsInvocation;
			if (!bool.TryParse(_configuration["AllowMethodsInvocation"], out allowMethodsInvocation))
			{
				allowMethodsInvocation = true;
			}

			return allowMethodsInvocation;
		}
	}
}