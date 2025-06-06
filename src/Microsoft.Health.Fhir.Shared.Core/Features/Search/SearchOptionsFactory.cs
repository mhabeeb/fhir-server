﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using EnsureThat;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search.Access;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers;
using Microsoft.Health.Fhir.Core.Models;
using Expression = Microsoft.Health.Fhir.Core.Features.Search.Expressions.Expression;

namespace Microsoft.Health.Fhir.Core.Features.Search
{
    public class SearchOptionsFactory : ISearchOptionsFactory
    {
        private static readonly string SupportedTotalTypes = $"'{TotalType.Accurate}', '{TotalType.None}'".ToLower(CultureInfo.CurrentCulture);

        private readonly IExpressionParser _expressionParser;
        private readonly RequestContextAccessor<IFhirRequestContext> _contextAccessor;
        private readonly ISortingValidator _sortingValidator;
        private readonly ExpressionAccessControl _expressionAccess;
        private readonly ISearchParameterDefinitionManager _searchParameterDefinitionManager;
        private readonly ILogger _logger;
        private readonly CoreFeatureConfiguration _featureConfiguration;
        private SearchParameterInfo _resourceTypeSearchParameter;
        private readonly HashSet<string> _queryHintParameterNames = new() { KnownQueryParameterNames.GlobalEndSurrogateId, KnownQueryParameterNames.EndSurrogateId, KnownQueryParameterNames.GlobalStartSurrogateId, KnownQueryParameterNames.StartSurrogateId };

        public SearchOptionsFactory(
            IExpressionParser expressionParser,
            ISearchParameterDefinitionManager.SearchableSearchParameterDefinitionManagerResolver searchParameterDefinitionManagerResolver,
            IOptions<CoreFeatureConfiguration> featureConfiguration,
            RequestContextAccessor<IFhirRequestContext> contextAccessor,
            ISortingValidator sortingValidator,
            ExpressionAccessControl expressionAccess,
            ILogger<SearchOptionsFactory> logger)
        {
            EnsureArg.IsNotNull(expressionParser, nameof(expressionParser));
            EnsureArg.IsNotNull(searchParameterDefinitionManagerResolver, nameof(searchParameterDefinitionManagerResolver));
            EnsureArg.IsNotNull(featureConfiguration?.Value, nameof(featureConfiguration));
            EnsureArg.IsNotNull(contextAccessor, nameof(contextAccessor));
            EnsureArg.IsNotNull(sortingValidator, nameof(sortingValidator));
            EnsureArg.IsNotNull(expressionAccess, nameof(expressionAccess));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _expressionParser = expressionParser;
            _contextAccessor = contextAccessor;
            _sortingValidator = sortingValidator;
            _expressionAccess = expressionAccess;
            _searchParameterDefinitionManager = searchParameterDefinitionManagerResolver();
            _logger = logger;
            _featureConfiguration = featureConfiguration.Value;
        }

        private SearchParameterInfo ResourceTypeSearchParameter
        {
            get
            {
                if (_resourceTypeSearchParameter == null)
                {
#if Stu3 || R4 || R4B
                    _resourceTypeSearchParameter = _searchParameterDefinitionManager.GetSearchParameter(ResourceType.Resource.ToString(), SearchParameterNames.ResourceType);
#else
                    _resourceTypeSearchParameter = _searchParameterDefinitionManager.GetSearchParameter(KnownResourceTypes.Resource, SearchParameterNames.ResourceType);
#endif
                }

                return _resourceTypeSearchParameter;
            }
        }

        public SearchOptions Create(string resourceType, IReadOnlyList<Tuple<string, string>> queryParameters, bool isAsyncOperation = false, ResourceVersionType resourceVersionTypes = ResourceVersionType.Latest, bool onlyIds = false, bool isIncludesOperation = false)
        {
            return Create(null, null, resourceType, queryParameters, isAsyncOperation, resourceVersionTypes: resourceVersionTypes, onlyIds: onlyIds, isIncludesOperation: isIncludesOperation);
        }

        [SuppressMessage("Design", "CA1308", Justification = "ToLower() is required to format parameter output correctly.")]
        public SearchOptions Create(
            string compartmentType,
            string compartmentId,
            string resourceType,
            IReadOnlyList<Tuple<string, string>> queryParameters,
            bool isAsyncOperation = false,
            bool useSmartCompartmentDefinition = false,
            ResourceVersionType resourceVersionTypes = ResourceVersionType.Latest,
            bool onlyIds = false,
            bool isIncludesOperation = false)
        {
            var searchOptions = new SearchOptions();

            if (queryParameters != null && queryParameters.Any(_ => _.Item1 == KnownQueryParameterNames.GlobalEndSurrogateId && _.Item2 != null))
            {
                var queryHint = new List<(string param, string value)>();

                foreach (var par in queryParameters.Where(x => x.Item1 == KnownQueryParameterNames.Type || _queryHintParameterNames.Contains(x.Item1)))
                {
                    queryHint.Add((par.Item1, par.Item2));
                }

                searchOptions.QueryHints = queryHint;
            }

            searchOptions.IgnoreSearchParamHash = queryParameters != null && queryParameters.Any(_ => _.Item1 == KnownQueryParameterNames.IgnoreSearchParamHash && _.Item2 != null);

            string continuationToken = null;
            string feedRange = null;

            // $includes related parameters
            string includesContinuationToken = null;
            int? includesCount = null;

            var searchParams = new SearchParams();
            var unsupportedSearchParameters = new List<Tuple<string, string>>();
            bool setDefaultBundleTotal = true;
            bool notReferencedSearch = false;

            // Extract the continuation token, filter out the other known query parameters that's not search related.
            // Exclude time travel parameters from evaluation to avoid warnings about unsupported parameters
            foreach (Tuple<string, string> query in queryParameters?.Where(_ => !_queryHintParameterNames.Contains(_.Item1)) ?? Enumerable.Empty<Tuple<string, string>>())
            {
                if (query.Item1 == KnownQueryParameterNames.ContinuationToken)
                {
                    // This is an unreachable case. The mapping of the query parameters makes it so only one continuation token can exist.
                    if (continuationToken != null)
                    {
                        throw new InvalidSearchOperationException(
                            string.Format(Core.Resources.MultipleQueryParametersNotAllowed, KnownQueryParameterNames.ContinuationToken));
                    }

                    continuationToken = ContinuationTokenEncoder.Decode(query.Item2);
                    setDefaultBundleTotal = false;
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.FeedRange, StringComparison.OrdinalIgnoreCase))
                {
                    feedRange = query.Item2;
                }
                else if (query.Item1 == KnownQueryParameterNames.Format || query.Item1 == KnownQueryParameterNames.Pretty)
                {
                    // _format and _pretty are not search parameters, so we can ignore them.
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.Type, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(query.Item2))
                    {
                        throw new BadRequestException(string.Format(Core.Resources.InvalidTypeParameter, query.Item2));
                    }

                    var types = query.Item2.SplitByOrSeparator();
                    var badTypes = types.Where(type => !ModelInfoProvider.IsKnownResource(type)).ToHashSet();

                    if (badTypes.Count != 0)
                    {
                        _contextAccessor.RequestContext?.BundleIssues.Add(
                            new OperationOutcomeIssue(
                                OperationOutcomeConstants.IssueSeverity.Warning,
                                OperationOutcomeConstants.IssueType.NotSupported,
                                string.Format(Core.Resources.InvalidTypeParameter, badTypes.OrderBy(x => x).Select(type => $"'{type}'").JoinByOrSeparator())));
                        if (badTypes.Count != types.Count)
                        {
                            // In case of we have acceptable types, we filter invalid types from search.
                            searchParams.Add(KnownQueryParameterNames.Type, types.Except(badTypes).JoinByOrSeparator());
                        }
                        else
                        {
                            // If all types are invalid, we add them to search params. If we remove them, we wouldn't filter by type, and return all types,
                            // which is incorrect behaviour. Optimally we should indicate in search options what it would yield nothing, and skip search,
                            // but there is no option for that right now.
                            searchParams.Add(KnownQueryParameterNames.Type, query.Item2);
                        }
                    }
                    else
                    {
                        searchParams.Add(KnownQueryParameterNames.Type, query.Item2);
                    }
                }
                else if (string.IsNullOrWhiteSpace(query.Item1) || string.IsNullOrWhiteSpace(query.Item2))
                {
                    // Query parameter with empty value is not supported.
                    unsupportedSearchParameters.Add(query);
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.Text, StringComparison.OrdinalIgnoreCase))
                {
                    // Query parameter _text is not allowed for any resource.
                    unsupportedSearchParameters.Add(query);
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.Total, StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse<TotalType>(query.Item2, true, out var totalType))
                    {
                        ValidateTotalType(totalType);

                        searchOptions.IncludeTotal = totalType;
                        setDefaultBundleTotal = false;
                    }
                    else
                    {
                        throw new BadRequestException(string.Format(Core.Resources.InvalidTotalParameter, query.Item2, SupportedTotalTypes));
                    }
                }
                else if (query.Item1 == KnownQueryParameterNames.Count && Convert.ToInt32(query.Item2) == 0)
                {
                    try
                    {
                        searchParams.Add(KnownQueryParameterNames.Summary, SummaryType.Count.ToString());
                    }
                    catch (Exception ex)
                    {
                        throw new BadRequestException(ex.Message);
                    }
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.NotReferenced, StringComparison.OrdinalIgnoreCase))
                {
                    // Currently the only supported value for _not-referenced is "*:*".
                    // If this feature is enhanced in the future to allow for checking if specific references are not present, this will need to be updated.
                    if (string.Equals(query.Item2, "*:*", StringComparison.OrdinalIgnoreCase))
                    {
                        notReferencedSearch = true;
                                            }
                    else
                    {
                        _contextAccessor.RequestContext?.BundleIssues.Add(
                            new OperationOutcomeIssue(
                                OperationOutcomeConstants.IssueSeverity.Warning,
                                OperationOutcomeConstants.IssueType.NotSupported,
                                Core.Resources.NotReferencedParameterInvalidValue));
                     }
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.IncludesContinuationToken, StringComparison.OrdinalIgnoreCase))
                {
                    // This is an unreachable case. The mapping of the query parameters makes it so only one continuation token can exist.
                    if (includesContinuationToken != null)
                    {
                        throw new InvalidSearchOperationException(
                            string.Format(Core.Resources.MultipleQueryParametersNotAllowed, KnownQueryParameterNames.IncludesContinuationToken));
                    }

                    if (isIncludesOperation)
                    {
                        includesContinuationToken = ContinuationTokenEncoder.Decode(query.Item2);
                        setDefaultBundleTotal = false;
                    }
                    else
                    {
                        _contextAccessor.RequestContext?.BundleIssues.Add(
                            new OperationOutcomeIssue(
                                OperationOutcomeConstants.IssueSeverity.Information,
                                OperationOutcomeConstants.IssueType.Informational,
                                Core.Resources.IncludesContinuationTokenIgnored));
                    }
                }
                else if (string.Equals(query.Item1, KnownQueryParameterNames.IncludesCount, StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(query.Item2, out int count) && count > 0)
                    {
                        includesCount = count;
                    }
                    else
                    {
                        throw new BadRequestException(Core.Resources.InvalidSearchIncludesCountSpecified);
                    }
                }
                else
                {
                    // Parse the search parameters.
                    try
                    {
                        // Basic format checking (e.g. integer value for _count key etc.).
                        searchParams.Add(query.Item1, query.Item2);
                    }
                    catch (Exception ex)
                    {
                        throw new BadRequestException(ex.Message);
                    }
                }
            }

            if (isIncludesOperation && string.IsNullOrEmpty(includesContinuationToken))
            {
                throw new BadRequestException(Core.Resources.MissingIncludesContinuationToken);
            }

            searchOptions.ContinuationToken = continuationToken;
            searchOptions.IncludesContinuationToken = includesContinuationToken;
            searchOptions.IncludesOperationSupported = _featureConfiguration.SupportsIncludes;
            searchOptions.FeedRange = feedRange;

            if (setDefaultBundleTotal)
            {
                ValidateTotalType(_featureConfiguration.IncludeTotalInBundle);
                searchOptions.IncludeTotal = _featureConfiguration.IncludeTotalInBundle;
            }

            // Check the item count.
            if (searchParams.Count != null)
            {
                searchOptions.MaxItemCountSpecifiedByClient = true;

                if (isAsyncOperation)
                {
                    searchOptions.IsAsyncOperation = true;
                    searchOptions.MaxItemCount = searchParams.Count.Value;
                }
                else if (searchParams.Count > _featureConfiguration.MaxItemCountPerSearch)
                {
                    searchOptions.MaxItemCount = _featureConfiguration.MaxItemCountPerSearch;

                    _contextAccessor.RequestContext?.BundleIssues.Add(
                        new OperationOutcomeIssue(
                            OperationOutcomeConstants.IssueSeverity.Information,
                            OperationOutcomeConstants.IssueType.Informational,
                            string.Format(Core.Resources.SearchParamaterCountExceedLimit, _featureConfiguration.MaxItemCountPerSearch, searchParams.Count)));
                }
                else
                {
                    searchOptions.MaxItemCount = searchParams.Count.Value;
                }
            }
            else
            {
                searchOptions.MaxItemCount = _featureConfiguration.DefaultItemCountPerSearch;
            }

            if (includesCount.HasValue && includesCount <= _featureConfiguration.MaxIncludeCountPerSearch)
            {
                searchOptions.IncludeCount = includesCount.Value;
            }
            else
            {
                if (includesCount.HasValue)
                {
                    searchOptions.IncludeCount = _featureConfiguration.MaxIncludeCountPerSearch;
                    _contextAccessor.RequestContext?.BundleIssues.Add(
                        new OperationOutcomeIssue(
                            OperationOutcomeConstants.IssueSeverity.Information,
                            OperationOutcomeConstants.IssueType.Informational,
                            string.Format(Core.Resources.SearchParamaterIncludesCountExceedLimit, _featureConfiguration.MaxIncludeCountPerSearch, includesCount)));
                }
                else
                {
                    searchOptions.IncludeCount = _featureConfiguration.DefaultIncludeCountPerSearch;
                }
            }

            if (searchParams.Elements?.Any() == true && searchParams.Summary != null && searchParams.Summary != SummaryType.False)
            {
                // The search parameters _elements and _summarize cannot be specified for the same request.
                throw new BadRequestException(string.Format(Core.Resources.ElementsAndSummaryParametersAreIncompatible, KnownQueryParameterNames.Summary, KnownQueryParameterNames.Elements));
            }

            searchOptions.OnlyIds = onlyIds;

            // Check to see if only the count should be returned
            searchOptions.CountOnly = searchParams.Summary == SummaryType.Count;

            // If the resource type is not specified, then the common
            // search parameters should be used.
            string[] parsedResourceTypes = new[] { KnownResourceTypes.DomainResource };

            var searchExpressions = new List<Expression>();
            if (string.IsNullOrWhiteSpace(resourceType))
            {
                // Try to parse resource types from _type Search Parameter
                // This will result in empty array if _type has any modifiers
                // Which is good, since :not modifier changes the meaning of the
                // search parameter and we can no longer use it to deduce types
                // (and should proceed with ResourceType.DomainResource in that case)
                var resourceTypes = searchParams.Parameters
                    .Where(q => q.Item1 == KnownQueryParameterNames.Type) // <-- Equality comparison to avoid modifiers
                    .SelectMany(q => q.Item2.SplitByOrSeparator())
                    .Where(q => ModelInfoProvider.IsKnownResource(q))
                    .Distinct().ToList();

                if (resourceTypes.Any())
                {
                    parsedResourceTypes = resourceTypes.ToArray();
                }
            }
            else
            {
                parsedResourceTypes[0] = resourceType;
                if (!ModelInfoProvider.IsKnownResource(resourceType))
                {
                    throw new ResourceNotSupportedException(resourceType);
                }

                searchExpressions.Add(Expression.SearchParameter(ResourceTypeSearchParameter, Expression.StringEquals(FieldName.TokenCode, null, resourceType, false)));
            }

            CheckFineGrainedAccessControl(searchExpressions);

            var resourceTypesString = parsedResourceTypes.Select(x => x.ToString()).ToArray();

            searchExpressions.AddRange(searchParams.Parameters.Select(
            q =>
            {
                try
                {
                    return _expressionParser.Parse(resourceTypesString, q.Item1, q.Item2);
                }
                catch (SearchParameterNotSupportedException)
                {
                    unsupportedSearchParameters.Add(q);

                    return null;
                }
            })
            .Where(item => item != null));

            // Parse _include:iterate (_include:recurse) parameters.
            // _include:iterate (_include:recurse) expression may appear without a preceding _include parameter
            // when applied on a circular reference
            searchExpressions.AddRange(ParseIncludeIterateExpressions(searchParams.Include, resourceTypesString, false).Where(e => e != null));
            searchExpressions.AddRange(ParseIncludeIterateExpressions(searchParams.RevInclude, resourceTypesString, true).Where(e => e != null));

            if (!string.IsNullOrWhiteSpace(compartmentType))
            {
                if (Enum.TryParse(compartmentType, out CompartmentType parsedCompartmentType))
                {
                    if (string.IsNullOrWhiteSpace(compartmentId))
                    {
                        throw new InvalidSearchOperationException(Core.Resources.CompartmentIdIsInvalid);
                    }

                    if (useSmartCompartmentDefinition)
                    {
                        searchExpressions.Add(Expression.SmartCompartmentSearch(compartmentType, compartmentId, resourceTypesString));
                    }
                    else
                    {
                        searchExpressions.Add(Expression.CompartmentSearch(compartmentType, compartmentId, resourceTypesString));
                    }
                }
                else
                {
                    throw new InvalidSearchOperationException(string.Format(Core.Resources.CompartmentTypeIsInvalid, compartmentType));
                }
            }

            if (!string.IsNullOrWhiteSpace(_contextAccessor.RequestContext?.AccessControlContext?.CompartmentResourceType))
            {
                var smartCompartmentType = _contextAccessor.RequestContext?.AccessControlContext?.CompartmentResourceType;
                var smartCompartmentId = _contextAccessor.RequestContext?.AccessControlContext?.CompartmentId;

                if (Enum.TryParse(smartCompartmentType, out CompartmentType parsedCompartmentType))
                {
                    if (string.IsNullOrWhiteSpace(smartCompartmentId))
                    {
                        throw new InvalidSearchOperationException(
                            string.Format(Core.Resources.FhirUserClaimIsNotAValidResource, _contextAccessor.RequestContext?.AccessControlContext.FhirUserClaim));
                    }

                    // Don't add the smart compartment twice. this is a patch for bug number AB#152447.
                    if (!searchExpressions.Any(e => e.ValueInsensitiveEquals(Expression.SmartCompartmentSearch(smartCompartmentType, smartCompartmentId, null))))
                    {
                        searchExpressions.Add(Expression.SmartCompartmentSearch(smartCompartmentType, smartCompartmentId, null));
                    }
                }
                else
                {
                    throw new InvalidSearchOperationException(
                            string.Format(Core.Resources.FhirUserClaimIsNotAValidResource, _contextAccessor.RequestContext?.AccessControlContext.FhirUserClaim));
                }
            }

            if (notReferencedSearch)
            {
                searchExpressions.Add(Expression.NotReferenced());
            }

            if (searchExpressions.Count == 1)
            {
                searchOptions.Expression = searchExpressions[0];
            }
            else if (searchExpressions.Count > 1)
            {
                searchOptions.Expression = Expression.And(searchExpressions.ToArray());
            }

            searchOptions.UnsupportedSearchParams = unsupportedSearchParameters;

            var searchSortErrors = new List<string>();

            // Sort is unneded for summary count
            if (searchParams.Sort?.Count > 0 && searchParams.Summary != SummaryType.Count)
            {
                var sortings = new List<(SearchParameterInfo, SortOrder)>(searchParams.Sort.Count);
                bool sortingsValid = true;

                // Only parameters that are valid for searching can also be used as sort parameter values. Therefore first check if the sort parameter values are valid as search parameters.
                foreach ((string, Hl7.Fhir.Rest.SortOrder) sorting in searchParams.Sort)
                {
                    try
                    {
                        SearchParameterInfo searchParameterInfo = resourceTypesString.Select(t => _searchParameterDefinitionManager.GetSearchParameter(t, sorting.Item1)).Distinct().First();
                        sortings.Add((searchParameterInfo, sorting.Item2.ToCoreSortOrder()));
                    }
                    catch (SearchParameterNotSupportedException)
                    {
                        sortingsValid = false;
                        searchSortErrors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SortParameterValueIsNotValidSearchParameter, sorting.Item1, string.Join(", ", resourceTypesString)));
                    }
                }

                // Sort parameter values are valid search parameters. Now verify that sort parameter values are also valid for sorting.
                if (sortingsValid)
                {
                    if (!_sortingValidator.ValidateSorting(sortings, out IReadOnlyList<string> errorMessages))
                    {
                        // Sanity check, ValidateSorting must output errors if it returns false.
                        if (errorMessages == null || errorMessages.Count == 0)
                        {
                            throw new InvalidOperationException($"Expected {_sortingValidator.GetType().Name} to return error messages when {nameof(_sortingValidator.ValidateSorting)} returns false");
                        }

                        sortingsValid = false;

                        foreach (var errorMessage in errorMessages)
                        {
                            searchSortErrors.Add(errorMessage);
                        }
                    }
                }

                if (sortingsValid)
                {
                    searchOptions.Sort = sortings;
                }
            }

            if (searchOptions.Sort == null)
            {
                searchOptions.Sort = Array.Empty<(SearchParameterInfo searchParameterInfo, SortOrder sortOrder)>();
            }

            searchOptions.ResourceVersionTypes = resourceVersionTypes;

            // Processing of parameters is finished. If any of the parameters are unsupported warning is put into the bundle or exception is thrown,
            // depending on the state of the "Prefer" header.
            if (unsupportedSearchParameters.Any() || searchSortErrors.Any())
            {
                var allErrors = new List<string>();
                foreach (Tuple<string, string> unsupported in unsupportedSearchParameters)
                {
                    allErrors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SearchParameterNotSupported, unsupported.Item1, string.Join(",", resourceTypesString)));
                }

                allErrors.AddRange(searchSortErrors);

                if (_contextAccessor.GetIsStrictHandlingEnabled())
                {
                    throw new BadRequestException(allErrors);
                }

                // There is no "Prefer" header with handling value, or handling value is valid and not set to "Prefer: handling=strict".
                foreach (string error in allErrors)
                {
                    _contextAccessor.RequestContext?.BundleIssues.Add(new OperationOutcomeIssue(
                            OperationOutcomeConstants.IssueSeverity.Warning,
                            OperationOutcomeConstants.IssueType.NotSupported,
                            error));
                }
            }

            _expressionAccess.CheckAndRaiseAccessExceptions(searchOptions.Expression);

            try
            {
                LogExpresssionSearchParameters(searchOptions.Expression);
            }
            catch (Exception e)
            {
                _logger.LogWarning("Unable to log search parameters. Error: {Exception}", e.ToString());
            }

            return searchOptions;
        }

        private IEnumerable<IncludeExpression> ParseIncludeIterateExpressions(IList<(string query, IncludeModifier modifier)> includes, string[] typesString, bool isReversed)
        {
            return includes.Select(p =>
            {
                var includeResourceTypeList = typesString;
                var iterate = p.modifier == IncludeModifier.Iterate || p.modifier == IncludeModifier.Recurse;

                if (iterate)
                {
                    var includeResourceType = p.query?.Split(':')[0];
                    if (!ModelInfoProvider.IsKnownResource(includeResourceType))
                    {
                        throw new ResourceNotSupportedException(includeResourceType);
                    }

                    includeResourceTypeList = new[] { includeResourceType };
                }

                IReadOnlyCollection<string> allowedResourceTypesByScope = null;
                if (_contextAccessor.RequestContext?.AccessControlContext?.ApplyFineGrainedAccessControl == true)
                {
                    allowedResourceTypesByScope = _contextAccessor.RequestContext?.AccessControlContext?.AllowedResourceActions.Select(s => s.Resource).ToList();
                }

                var expression = _expressionParser.ParseInclude(includeResourceTypeList, p.query, isReversed, iterate, allowedResourceTypesByScope);

                // Reversed Iterate expressions (not wildcard) must specify target type if there is more than one possible target type
                if (expression.Reversed && expression.Iterate && expression.TargetResourceType == null &&
                    expression.ReferenceSearchParameter?.TargetResourceTypes?.Count > 1)
                {
                    throw new BadRequestException(
                        string.Format(Core.Resources.RevIncludeIterateTargetTypeNotSpecified, p.query));
                }

                if (expression.TargetResourceType != null &&
                   string.IsNullOrWhiteSpace(expression.TargetResourceType))
                {
                    throw new BadRequestException(
                        string.Format(Core.Resources.IncludeRevIncludeInvalidTargetResourceType, expression.TargetResourceType));
                }

                if (expression.TargetResourceType != null && !ModelInfoProvider.IsKnownResource(expression.TargetResourceType))
                {
                    throw new ResourceNotSupportedException(expression.TargetResourceType);
                }

                // For circular include iterate expressions, add an informational issue indicating that a single iteration is supported.
                // See https://www.hl7.org/fhir/search.html#revinclude.
                if (expression.Iterate && expression.CircularReference)
                {
                    var issueProperty = string.Concat(isReversed ? "_revinclude" : "_include", ":", p.modifier.ToString().ToLowerInvariant());
                    _contextAccessor.RequestContext?.BundleIssues.Add(
                        new OperationOutcomeIssue(
                            OperationOutcomeConstants.IssueSeverity.Information,
                            OperationOutcomeConstants.IssueType.Informational,
                            string.Format(Core.Resources.IncludeIterateCircularReferenceExecutedOnce, issueProperty, p.query)));
                }

                if (_contextAccessor.RequestContext?.AccessControlContext?.ApplyFineGrainedAccessControl == true && !allowedResourceTypesByScope.Contains(KnownResourceTypes.All))
                {
                    if (expression.TargetResourceType != null && !allowedResourceTypesByScope.Contains(expression.TargetResourceType))
                    {
                        _logger.LogTrace("Query restricted by clinical scopes.  Target resource type {ResourceType} not included in allowed resources.", expression.TargetResourceType);
                        return null;
                    }

                    if (!expression.Produces.Any())
                    {
                        return null;
                    }
                }

                return expression;
            });
        }

        private static void ValidateTotalType(TotalType totalType)
        {
            // Estimate is not yet supported.
            if (totalType == TotalType.Estimate)
            {
                throw new SearchOperationNotSupportedException(string.Format(Core.Resources.UnsupportedTotalParameter, totalType, SupportedTotalTypes));
            }
        }

        private void LogExpresssionSearchParameters(Expression expression)
        {
            if (expression == null)
            {
                return;
            }
            else if (expression is SearchParameterExpression baseSearchParameterExpression)
            {
                LogSearchParameterData(baseSearchParameterExpression.Parameter.Url);
                LogExpresssionSearchParameters(baseSearchParameterExpression.Expression);
            }
            else if (expression is SearchParameterExpressionBase baseExpression)
            {
                LogSearchParameterData(baseExpression.Parameter.Url);
            }
            else if (expression is MissingSearchParameterExpression missingSearchParameterExpression)
            {
                LogSearchParameterData(missingSearchParameterExpression.Parameter.Url, missingSearchParameterExpression.IsMissing);
            }
            else if (expression is ChainedExpression chainedExpression)
            {
                LogSearchParameterData(chainedExpression.ReferenceSearchParameter.Url);
                LogExpresssionSearchParameters(chainedExpression.Expression);
            }
            else if (expression is SearchParameterExpression searchParameterExpression)
            {
                LogSearchParameterData(searchParameterExpression.Parameter.Url);
                LogExpresssionSearchParameters(searchParameterExpression.Expression);
            }
            else if (expression is MultiaryExpression multiaryExpression)
            {
                foreach (var subExpression in multiaryExpression.Expressions)
                {
                    LogExpresssionSearchParameters(subExpression);
                }
            }
            else if (expression is UnionExpression unionExpression)
            {
                foreach (var subExpression in unionExpression.Expressions)
                {
                    LogExpresssionSearchParameters(subExpression);
                }
            }
            else if (expression is NotExpression notExpression)
            {
                LogExpresssionSearchParameters(notExpression.Expression);
            }
            else if (expression is SortExpression sortExpression)
            {
                LogSearchParameterData(sortExpression.Parameter.Url);
            }
            else if (expression is IncludeExpression includeExpression)
            {
                LogSearchParameterData(includeExpression.ReferenceSearchParameter?.Url);
            }
        }

        private void LogSearchParameterData(Uri url, bool isMissing = false)
        {
            string logOutput = string.Format("SearchParameters in search. Url: {0}.", url?.OriginalString);

            if (isMissing)
            {
                logOutput = logOutput + string.Format(" IsMissing: {0}.", isMissing);
            }

            _logger.LogInformation(logOutput);
        }

        private void CheckFineGrainedAccessControl(List<Expression> searchExpressions)
        {
            // check resource type restrictions from SMART clinical scopes
            if (_contextAccessor.RequestContext?.AccessControlContext?.ApplyFineGrainedAccessControl == true)
            {
                bool allowAllResourceTypes = false;
                var clinicalScopeResources = new List<ResourceType>();

                foreach (ScopeRestriction restriction in _contextAccessor.RequestContext?.AccessControlContext.AllowedResourceActions)
                {
                    if (restriction.Resource == KnownResourceTypes.All)
                    {
                        allowAllResourceTypes = true;
                        break;
                    }

                    if (!Enum.TryParse<ResourceType>(restriction.Resource, out var clinicalScopeResourceType))
                    {
                        throw new ResourceNotSupportedException(restriction.Resource);
                    }

                    clinicalScopeResources.Add(clinicalScopeResourceType);
                }

                if (!allowAllResourceTypes)
                {
                    if (clinicalScopeResources.Any())
                    {
                        searchExpressions.Add(Expression.SearchParameter(ResourceTypeSearchParameter, Expression.In(FieldName.TokenCode, null, clinicalScopeResources)));
                    }
                    else // block all queries
                    {
                        searchExpressions.Add(Expression.SearchParameter(ResourceTypeSearchParameter, Expression.StringEquals(FieldName.TokenCode, null, "none", false)));
                    }
                }
            }
        }
    }
}
