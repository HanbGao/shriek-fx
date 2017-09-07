﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Internal;
using Microsoft.AspNetCore.Mvc.Routing;
using System.Net.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Shriek.Samples.CQRS.EFCore
{
    public class Convention<TService> : IControllerModelConvention, IActionModelConvention, IParameterModelConvention
    {
        public void Apply(ControllerModel controller)
        {
            if (!typeof(TService).IsAssignableFrom(controller.ControllerType)) return;

            var attrs = typeof(TService).GetCustomAttributes();
            var controllerAttrs = new List<object>();

            foreach (var att in attrs)
            {
                if (att is WebApi.Proxy.RouteAttribute routeAttr)
                {
                    var template = routeAttr.Template;
                    var routeAttribute = Activator.CreateInstance(typeof(RouteAttribute), template);
                    controllerAttrs.Add(routeAttribute);
                }
            }

            if (controllerAttrs.Any())
            {
                controller.Selectors.Clear();
                AddRange(controller.Selectors, CreateSelectors(controllerAttrs));
            }
        }

        public void Apply(ActionModel action)
        {
            if (!typeof(TService).IsAssignableFrom(action.Controller.ControllerType)) return;

            var actionParams = action.ActionMethod.GetParameters();

            var method = typeof(TService).GetMethods().FirstOrDefault(mth =>
            {
                var mthParams = mth.GetParameters();
                return action.ActionMethod.Name == mth.Name
                       && actionParams.Length == mthParams.Length
                       && actionParams.Any(x => mthParams.Any(o => x.Name == o.Name && x.GetType() == o.GetType()));
            });

            var attrs = method.GetCustomAttributes();
            var actionAttrs = new List<object>();

            foreach (var att in attrs)
            {
                if (att is WebApi.Proxy.HttpMethodAttribute methodAttr)
                {
                    var httpMethod = methodAttr.Method;
                    var path = methodAttr.Path;

                    if (httpMethod == HttpMethod.Get)
                    {
                        var methodAttribute = Activator.CreateInstance(typeof(HttpGetAttribute), path);
                        actionAttrs.Add(methodAttribute);
                    }
                    else if (httpMethod == HttpMethod.Post)
                    {
                        var methodAttribute = Activator.CreateInstance(typeof(HttpPostAttribute), path);
                        actionAttrs.Add(methodAttribute);
                    }
                    else if (httpMethod == HttpMethod.Put)
                    {
                        var methodAttribute = Activator.CreateInstance(typeof(HttpPutAttribute), path);
                        actionAttrs.Add(methodAttribute);
                    }
                    else if (httpMethod == HttpMethod.Delete)
                    {
                        var methodAttribute = Activator.CreateInstance(typeof(HttpDeleteAttribute), path);
                        actionAttrs.Add(methodAttribute);
                    }
                    else if (httpMethod == HttpMethod.Head)
                    {
                        var methodAttribute = Activator.CreateInstance(typeof(HttpHeadAttribute), path);
                        actionAttrs.Add(methodAttribute);
                    }
                    else if (httpMethod == HttpMethod.Options)
                    {
                        var methodAttribute = Activator.CreateInstance(typeof(HttpOptionsAttribute), path);
                        actionAttrs.Add(methodAttribute);
                    }
                }
                if (att is WebApi.Proxy.RouteAttribute routeAttr)
                {
                    var template = routeAttr.Template;
                    var routeAttribute = Activator.CreateInstance(typeof(RouteAttribute), template);
                    actionAttrs.Add(routeAttribute);
                }
            }

            if (actionAttrs.Any())
            {
                action.Selectors.Clear();
                AddRange(action.Selectors, CreateSelectors(actionAttrs));
            }
        }

        public void Apply(ParameterModel parameter)
        {
            if (!typeof(TService).IsAssignableFrom(parameter.Action.Controller.ControllerType)) return;

            var actionParams = parameter.Action.ActionMethod.GetParameters();

            var method = typeof(TService).GetMethods().FirstOrDefault(mth =>
            {
                var mthParams = mth.GetParameters();
                return parameter.Action.ActionMethod.Name == mth.Name
                       && actionParams.Length == mthParams.Length
                       && actionParams.Any(x => mthParams.Any(o => x.Name == o.Name && x.GetType() == o.GetType()));
            });

            var theParam = method.GetParameters().FirstOrDefault(x => x.GetType() == parameter.ParameterInfo.GetType());

            if (theParam == null) return;

            var attrs = theParam.GetCustomAttributes();
            var paramAttrs = new List<object>();

            foreach (var att in attrs)
            {
                if (att is WebApi.Proxy.JsonContentAttribute)
                {
                    var paramAttribute = Activator.CreateInstance(typeof(FromBodyAttribute));

                    paramAttrs.Add(paramAttribute);
                }
            }

            if (paramAttrs.Any())
            {
                var parameterModel = CreateParameterModel(parameter.ParameterInfo, paramAttrs);
                parameter.BindingInfo = parameterModel.BindingInfo;
            }
        }

        /// <summary>
        /// Creates a <see cref="ParameterModel"/> for the given <see cref="ParameterInfo"/>.
        /// </summary>
        /// <param name="parameterInfo">The <see cref="ParameterInfo"/>.</param>
        /// <returns>A <see cref="ParameterModel"/> for the given <see cref="ParameterInfo"/>.</returns>
        protected virtual ParameterModel CreateParameterModel(ParameterInfo parameterInfo, IList<object> objects)
        {
            if (parameterInfo == null)
            {
                throw new ArgumentNullException(nameof(parameterInfo));
            }

            // CoreCLR returns IEnumerable<Attribute> from GetCustomAttributes - the OfType<object>
            // is needed to so that the result of ToArray() is object
            var attributes = parameterInfo.GetCustomAttributes(inherit: true).Concat(objects).ToList();
            var parameterModel = new ParameterModel(parameterInfo, attributes);

            var bindingInfo = BindingInfo.GetBindingInfo(attributes);
            parameterModel.BindingInfo = bindingInfo;

            parameterModel.ParameterName = parameterInfo.Name;

            return parameterModel;
        }

        private ICollection<SelectorModel> CreateSelectors(IList<object> attributes)
        {
            // Route attributes create multiple selector models, we want to split the set of
            // attributes based on these so each selector only has the attributes that affect it.
            //
            // The set of route attributes are split into those that 'define' a route versus those that are
            // 'silent'.
            //
            // We need to define a selector for each attribute that 'defines' a route, and a single selector
            // for all of the ones that don't (if any exist).
            //
            // If the attribute that 'defines' a route is NOT an IActionHttpMethodProvider, then we'll include with
            // it, any IActionHttpMethodProvider that are 'silent' IRouteTemplateProviders. In this case the 'extra'
            // action for silent route providers isn't needed.
            //
            // Ex:
            // [HttpGet]
            // [AcceptVerbs("POST", "PUT")]
            // [HttpPost("Api/Things")]
            // public void DoThing()
            //
            // This will generate 2 selectors:
            // 1. [HttpPost("Api/Things")]
            // 2. [HttpGet], [AcceptVerbs("POST", "PUT")]
            //
            // Another example of this situation is:
            //
            // [Route("api/Products")]
            // [AcceptVerbs("GET", "HEAD")]
            // [HttpPost("api/Products/new")]
            //
            // This will generate 2 selectors:
            // 1. [AcceptVerbs("GET", "HEAD")]
            // 2. [HttpPost]
            //
            // Note that having a route attribute that doesn't define a route template _might_ be an error. We
            // don't have enough context to really know at this point so we just pass it on.
            var routeProviders = new List<IRouteTemplateProvider>();

            var createSelectorForSilentRouteProviders = false;
            foreach (var attribute in attributes)
            {
                if (attribute is IRouteTemplateProvider routeTemplateProvider)
                {
                    if (IsSilentRouteAttribute(routeTemplateProvider))
                    {
                        createSelectorForSilentRouteProviders = true;
                    }
                    else
                    {
                        routeProviders.Add(routeTemplateProvider);
                    }
                }
            }

            foreach (var routeProvider in routeProviders)
            {
                // If we see an attribute like
                // [Route(...)]
                //
                // Then we want to group any attributes like [HttpGet] with it.
                //
                // Basically...
                //
                // [HttpGet]
                // [HttpPost("Products")]
                // public void Foo() { }
                //
                // Is two selectors. And...
                //
                // [HttpGet]
                // [Route("Products")]
                // public void Foo() { }
                //
                // Is one selector.
                if (!(routeProvider is IActionHttpMethodProvider))
                {
                    createSelectorForSilentRouteProviders = false;
                }
            }

            var selectorModels = new List<SelectorModel>();
            if (routeProviders.Count == 0 && !createSelectorForSilentRouteProviders)
            {
                // Simple case, all attributes apply
                selectorModels.Add(CreateSelectorModel(route: null, attributes: attributes));
            }
            else
            {
                // Each of these routeProviders are the ones that actually have routing information on them
                // something like [HttpGet] won't show up here, but [HttpGet("Products")] will.
                foreach (var routeProvider in routeProviders)
                {
                    var filteredAttributes = new List<object>();
                    foreach (var attribute in attributes)
                    {
                        if (ReferenceEquals(attribute, routeProvider))
                        {
                            filteredAttributes.Add(attribute);
                        }
                        else if (InRouteProviders(routeProviders, attribute))
                        {
                            // Exclude other route template providers
                            // Example:
                            // [HttpGet("template")]
                            // [Route("template/{id}")]
                        }
                        else if (
                            routeProvider is IActionHttpMethodProvider &&
                            attribute is IActionHttpMethodProvider)
                        {
                            // Example:
                            // [HttpGet("template")]
                            // [AcceptVerbs("GET", "POST")]
                            //
                            // Exclude other http method providers if this route is an
                            // http method provider.
                        }
                        else
                        {
                            filteredAttributes.Add(attribute);
                        }
                    }

                    selectorModels.Add(CreateSelectorModel(routeProvider, filteredAttributes));
                }

                if (createSelectorForSilentRouteProviders)
                {
                    var filteredAttributes = new List<object>();
                    foreach (var attribute in attributes)
                    {
                        if (!InRouteProviders(routeProviders, attribute))
                        {
                            filteredAttributes.Add(attribute);
                        }
                    }

                    selectorModels.Add(CreateSelectorModel(route: null, attributes: filteredAttributes));
                }
            }

            return selectorModels;
        }

        private static bool InRouteProviders(List<IRouteTemplateProvider> routeProviders, object attribute)
        {
            foreach (var rp in routeProviders)
            {
                if (ReferenceEquals(rp, attribute))
                {
                    return true;
                }
            }

            return false;
        }

        private static SelectorModel CreateSelectorModel(IRouteTemplateProvider route, IList<object> attributes)
        {
            var selectorModel = new SelectorModel();
            if (route != null)
            {
                selectorModel.AttributeRouteModel = new AttributeRouteModel(route);
            }

            AddRange(selectorModel.ActionConstraints, attributes.OfType<IActionConstraintMetadata>());

            // Simple case, all HTTP method attributes apply
            var httpMethods = attributes
                .OfType<IActionHttpMethodProvider>()
                .SelectMany(a => a.HttpMethods)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (httpMethods.Length > 0)
            {
                selectorModel.ActionConstraints.Add(new HttpMethodActionConstraint(httpMethods));
            }

            return selectorModel;
        }

        private bool IsSilentRouteAttribute(IRouteTemplateProvider routeTemplateProvider)
        {
            return
                routeTemplateProvider.Template == null &&
                routeTemplateProvider.Order == null &&
                routeTemplateProvider.Name == null;
        }

        private static void AddRange<T>(IList<T> list, IEnumerable<T> items)
        {
            foreach (var item in items)
            {
                list.Add(item);
            }
        }
    }
}