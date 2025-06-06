﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.Client;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Tests.E2E.Rest.Search
{
    public class DateSearchTestFixture : HttpIntegrationTestFixture
    {
        public DateSearchTestFixture(DataStore dataStore, Format format, TestFhirServerFactory testFhirServerFactory)
            : base(dataStore, format, testFhirServerFactory)
        {
        }

        public Coding Coding { get; private set; }

        public IReadOnlyList<Observation> Observations { get; private set; }

        protected override async Task OnInitializedAsync()
        {
            // Creates a unique code for searches
            Coding = new Coding
            {
                Code = Guid.NewGuid().ToString(),
                System = "http://fhir-server-test/guid",
            };

            Observations = await TestFhirClient.CreateResourcesAsync<Observation>(
            p => SetObservation(p, "1979-12-31"),                // 1979-12-31T00:00:00.0000000 <-> 1979-12-31T23:59:59.9999999
            p => SetObservation(p, "1980"),                      // 1980-01-01T00:00:00.0000000 <-> 1980-12-31T23:59:59.9999999
            p => SetObservation(p, "1980-05"),                   // 1980-05-01T00:00:00.0000000 <-> 1980-05-31T23:59:59.9999999
            p => SetObservation(p, "1980-05-11"),                // 1980-05-11T00:00:00.0000000 <-> 1980-05-11T23:59:59.9999999
            p => SetObservation(p, "1980-05-11T16:32:15"),       // 1980-05-11T16:32:15.0000000 <-> 1980-05-11T16:32:15.9999999
            p => SetObservation(p, "1980-05-11T16:32:15.500"),   // 1980-05-11T16:32:15.5000000 <-> 1980-05-11T16:32:15.5000000
            p => SetObservation(p, "1981-01-01"),        // 1981-01-01T00:00:00.0000000 <-> 1981-12-31T23:59:59.9999999
            p => SetObservationWithPeriod(p, "1980-05-16", "1980-05-17")); // 1980-05-16T00:00:00.0000000 <-> 1980-05-17T23:59:59.9999999
            void SetObservation(Observation observation, string date)
            {
                observation.Status = ObservationStatus.Final;
                observation.Code = new CodeableConcept
                {
                    Coding = new List<Coding>
                    {
                        Coding,
                    },
                };
                observation.Effective = new FhirDateTime(date);
            }

            void SetObservationWithPeriod(Observation observation, string startDate, string endDate)
            {
                observation.Status = ObservationStatus.Final;
                observation.Code = new CodeableConcept
                {
                    Coding = new List<Coding>
                    {
                        Coding,
                    },
                };
                observation.Effective = new Period(new FhirDateTime(startDate), new FhirDateTime(endDate));
            }
        }
    }
}
