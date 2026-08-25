# OTLP/Logs Ingestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a new `POST /v1/logs` endpoint to `LogsPlatform.Web` that accepts OTLP/HTTP Protobuf log export requests from any standard OpenTelemetry SDK, maps each `LogRecord` onto the existing internal `IngestEventRequest` contract, and feeds the same `IngestionProcessor` pipeline the proprietary `POST /api/v1/ingest/events` endpoint already uses — with zero changes to that existing endpoint.

**Architecture:** Vendor the official `opentelemetry-proto` `.proto` files into `LogsPlatform.Web` and compile them at build time via `Grpc.Tools`/`Google.Protobuf` (NOT via any pre-built "OpenTelemetry.Proto" NuGet package — none exists for server-side consumption; see Global Constraints). A new `OtlpLogsController` reads the raw Protobuf body, parses it into the generated `ExportLogsServiceRequest` message, maps each `LogRecord` via a new pure-function `OtlpLogMapper`, and calls the existing `IngestionProcessor.ProcessAsync`/`IEventRepository.AddEventsAsync` — identical to how `IngestionController` already works. Auth and rate-limiting reuse the exact same `ApiKeyAuthenticationOptions.SchemeName` scheme and in-memory rate-counter pattern already in `IngestionController`, duplicated rather than extracted into a shared abstraction (it is ~10 lines; a shared service would be a premature abstraction for two call sites).

**Tech Stack:** .NET 10, ASP.NET Core, `Google.Protobuf` 3.36.0, `Grpc.Tools` 2.83.0 (build-time only, code generation, no gRPC runtime), xUnit, `OpenTelemetry` 1.18.0 + `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.18.0 (test-only, for the real-SDK-client validation test).

## Global Constraints

- New endpoint is `POST /v1/logs` (the standard OTLP/HTTP path), **parallel to** the existing `POST /api/v1/ingest/events` — the existing endpoint is not modified in any way.
- Protobuf only. No JSON support, no gRPC transport.
- Logs signal only. No Traces, no Metrics.
- Auth is the existing `X-Api-Key` header scheme (`ApiKeyAuthenticationOptions.SchemeName = "ApiKey"`, claim `ApiKeyAuthenticationHandler.ApplicationIdClaimType = "ApplicationId"`). No new auth mechanism.
- No separate OTel Collector process. `LogsPlatform.Web` is the OTLP receiver directly.
- **Decisive fact verified against the live `opentelemetry-dotnet` and `opentelemetry-proto` GitHub repos on 2026-08-24:** there is no publicly consumable "OpenTelemetry.Proto" NuGet package, and `OpenTelemetry.Exporter.OpenTelemetryProtocol` does **not** expose public `OpenTelemetry.Proto.*` message classes for a receiver to parse — as of the current version, that package's own OTLP encoding is done by a hand-rolled internal `ProtobufOtlpLogSerializer` (write-only, exporter-side, not a public deserialization API; confirmed by inspecting `src/OpenTelemetry.Exporter.OpenTelemetryProtocol/Implementation/Serializer/` in the `opentelemetry-dotnet` repo, which contains no `Google.Protobuf`-generated message classes at all). Task 1 therefore vendors the official `.proto` files from `open-telemetry/opentelemetry-proto` and compiles them via `Grpc.Tools`/`Google.Protobuf` — exactly the fallback the design doc's §2 already anticipated ("או ה-`.proto` הרשמי של OTLP, מקומפל דרך `Grpc.Tools`/`Google.Protobuf`").
- `service.name` (a standard OTel Resource attribute) is intentionally **not** mapped to `Application` — Application identity is established solely via the API Key, matching every other ingestion path in this project.
- Testing must include a real `OpenTelemetry`/`OpenTelemetry.Exporter.OpenTelemetryProtocol` SDK client sending a real log through `ILogger` (Task 5) — not just hand-built Protobuf requests. This exact pattern (real client, not curl) caught two real production bugs earlier this same day in this project.
- Field mapping table (from the design doc, `docs/superpowers/specs/2026-08-24-otlp-logs-ingestion-design.md` §3), reproduced here as the binding spec for Task 3:

| OTLP `LogRecord` (+ parent `Resource`) | `IngestEventRequest` field | Rule |
|---|---|---|
| `TimeUnixNano` | `Timestamp` | nanoseconds since Unix epoch → UTC `DateTime` |
| `SeverityNumber` (1-24) | `Severity` | band mapping: 1-4→Trace, 5-8→Debug, 9-12→Info, 13-16→Warn, 17-20→Error, 21-24→Fatal |
| `Body` | `Message` | if `AnyValue.StringValue`, use directly; otherwise JSON-serialize the value |
| `TraceId` / `SpanId` | `TraceId` / `SpanId` | raw bytes → lowercase hex string, `null` if empty |
| `Resource.Attributes["deployment.environment"]` | `Environment` | standard OTel attribute |
| `Attributes["exception.type"]` + `["exception.stacktrace"]` | `Exception` (`IngestExceptionRequest`) | standard OTel exception semantic convention |
| `Attributes["logsplatform.module"]` / `["logsplatform.screen_service"]` / `["logsplatform.process"]` / `["logsplatform.operation"]` | `Hierarchy` (`IngestHierarchyRequest`) | custom convention |
| `Attributes["logsplatform.customer_id"]` | `CustomerId` | custom convention |
| `Attributes["logsplatform.user_id"]` | `UserId` | custom convention |
| remaining `Attributes` | `Metadata` | pass through as-is |
| `Resource.Attributes["service.name"]` | — | **not mapped** (see above) |

Every other field on `IngestEventRequest` (`EventKey`, `Version`, `CorrelationId`, `ParentSpanId`, `DurationMs`, `MessageTemplate`) has no OTLP source per the design doc and stays `null`.

---

## Task 1: Vendor OTLP proto files and wire Protobuf codegen into LogsPlatform.Web

**Files:**
- Create: `src/LogsPlatform.Web/Otlp/opentelemetry/proto/common/v1/common.proto`
- Create: `src/LogsPlatform.Web/Otlp/opentelemetry/proto/resource/v1/resource.proto`
- Create: `src/LogsPlatform.Web/Otlp/opentelemetry/proto/logs/v1/logs.proto`
- Create: `src/LogsPlatform.Web/Otlp/opentelemetry/proto/collector/logs/v1/logs_service.proto`
- Modify: `src/LogsPlatform.Web/LogsPlatform.Web.csproj`
- Modify: `tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj`
- Test: `tests/LogsPlatform.Tests/Web/OtlpGeneratedTypesTests.cs`

**Interfaces:**
- Produces: the generated C# classes `OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceRequest`, `OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceResponse`, `OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsPartialSuccess`, `OpenTelemetry.Proto.Logs.V1.LogRecord`, `OpenTelemetry.Proto.Logs.V1.ResourceLogs`, `OpenTelemetry.Proto.Logs.V1.ScopeLogs`, `OpenTelemetry.Proto.Logs.V1.SeverityNumber`, `OpenTelemetry.Proto.Common.V1.AnyValue`, `OpenTelemetry.Proto.Common.V1.KeyValue`, `OpenTelemetry.Proto.Resource.V1.Resource` — all `public`, consumable from `LogsPlatform.Web` and (via its existing `ProjectReference`) from `LogsPlatform.Tests`.

- [ ] **Step 1: Write the failing test proving the generated types don't exist yet**

Create `tests/LogsPlatform.Tests/Web/OtlpGeneratedTypesTests.cs`:

```csharp
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;

namespace LogsPlatform.Tests.Web;

public class OtlpGeneratedTypesTests
{
    [Fact]
    public void ExportLogsServiceRequest_IsConstructibleAndRoundTripsThroughBytes()
    {
        var request = new ExportLogsServiceRequest();
        var resourceLogs = new ResourceLogs();
        var scopeLogs = new ScopeLogs();
        scopeLogs.LogRecords.Add(new LogRecord { SeverityNumber = SeverityNumber.Error });
        resourceLogs.ScopeLogs.Add(scopeLogs);
        request.ResourceLogs.Add(resourceLogs);

        var bytes = request.ToByteArray();
        var roundTripped = ExportLogsServiceRequest.Parser.ParseFrom(bytes);

        Assert.Single(roundTripped.ResourceLogs);
        Assert.Equal(SeverityNumber.Error, roundTripped.ResourceLogs[0].ScopeLogs[0].LogRecords[0].SeverityNumber);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter OtlpGeneratedTypesTests`
Expected: build error — `the type or namespace name 'OpenTelemetry' does not exist` (the namespace doesn't exist yet; no `.proto` files or package references are wired in).

- [ ] **Step 3: Vendor the four official `.proto` files verbatim**

Create `src/LogsPlatform.Web/Otlp/opentelemetry/proto/common/v1/common.proto`:

```protobuf
// Copyright 2019, OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

syntax = "proto3";

package opentelemetry.proto.common.v1;

option csharp_namespace = "OpenTelemetry.Proto.Common.V1";
option java_multiple_files = true;
option java_package = "io.opentelemetry.proto.common.v1";
option java_outer_classname = "CommonProto";
option go_package = "go.opentelemetry.io/proto/otlp/common/v1";

// Represents any type of attribute value. AnyValue may contain a
// primitive value such as a string or integer or it may contain an arbitrary nested
// object containing arrays, key-value lists and primitives.
message AnyValue {
  // The value is one of the listed fields. It is valid for all values to be unspecified
  // in which case this AnyValue is considered to be "empty".
  oneof value {
    string string_value = 1;
    bool bool_value = 2;
    int64 int_value = 3;
    double double_value = 4;
    ArrayValue array_value = 5;
    KeyValueList kvlist_value = 6;
    bytes bytes_value = 7;
    // Reference to the string value in ProfilesDictionary.string_table.
    //
    // Note: This is currently used exclusively in the Profiling signal.
    // Implementers of OTLP receivers for signals other than Profiling should
    // treat the presence of this value as a non-fatal issue.
    // Log an error or warning indicating an unexpected field intended for the
    // Profiling signal and process the data as if this value were absent or
    // empty, ignoring its semantic content for the non-Profiling signal.
    //
    // Status: [Alpha]
    int32 string_value_strindex = 8;
  }
}

// ArrayValue is a list of AnyValue messages. We need ArrayValue as a message
// since oneof in AnyValue does not allow repeated fields.
message ArrayValue {
  // Array of values. The array may be empty (contain 0 elements).
  repeated AnyValue values = 1;
}

// KeyValueList is a list of KeyValue messages. We need KeyValueList as a message
// since `oneof` in AnyValue does not allow repeated fields. Everywhere else where we need
// a list of KeyValue messages (e.g. in Span) we use `repeated KeyValue` directly to
// avoid unnecessary extra wrapping (which slows down the protocol). The 2 approaches
// are semantically equivalent.
message KeyValueList {
  // A collection of key/value pairs of key-value pairs. The list may be empty (may
  // contain 0 elements).
  //
  // The keys MUST be unique (it is not allowed to have more than one
  // value with the same key).
  // The behavior of software that receives duplicated keys can be unpredictable.
  repeated KeyValue values = 1;
}

// Represents a key-value pair that is used to store Span attributes, Link
// attributes, etc.
message KeyValue {
  // The key name of the pair.
  // key_strindex MUST NOT be set if key is used.
  string key = 1;

  // The value of the pair.
  AnyValue value = 2;

  // Reference to the string key in ProfilesDictionary.string_table.
  // key MUST NOT be set if key_strindex is used.
  //
  // Note: This is currently used exclusively in the Profiling signal.
  // Implementers of OTLP receivers for signals other than Profiling should
  // treat the presence of this key as a non-fatal issue.
  // Log an error or warning indicating an unexpected field intended for the
  // Profiling signal and process the data as if this value were absent or
  // empty, ignoring its semantic content for the non-Profiling signal.
  //
  // Status: [Alpha]
  int32 key_strindex = 3;
}

// InstrumentationScope is a message representing the instrumentation scope information
// such as the fully qualified name and version. 
message InstrumentationScope {
  // A name denoting the Instrumentation scope.
  // An empty instrumentation scope name means the name is unknown.
  string name = 1;

  // Defines the version of the instrumentation scope.
  // An empty instrumentation scope version means the version is unknown.
  string version = 2;

  // Additional attributes that describe the scope. [Optional].
  // Attribute keys MUST be unique (it is not allowed to have more than one
  // attribute with the same key).
  // The behavior of software that receives duplicated keys can be unpredictable.
  repeated KeyValue attributes = 3;

  // The number of attributes that were discarded. Attributes
  // can be discarded because their keys are too long or because there are too many
  // attributes. If this value is 0, then no attributes were dropped.
  uint32 dropped_attributes_count = 4;
}

// A reference to an Entity.
// Entity represents an object of interest associated with produced telemetry: e.g spans, metrics, profiles, or logs.
//
// Status: [Development]
message EntityRef {
  // The Schema URL, if known. This is the identifier of the Schema that the entity data
  // is recorded in. To learn more about Schema URL see
  // https://opentelemetry.io/docs/specs/otel/schemas/#schema-url
  //
  // This schema_url applies to the data in this message and to the Resource attributes
  // referenced by id_keys and description_keys.
  // TODO: discuss if we are happy with this somewhat complicated definition of what
  // the schema_url applies to.
  //
  // This field obsoletes the schema_url field in ResourceMetrics/ResourceSpans/ResourceLogs.
  string schema_url = 1;

  // Defines the type of the entity. MUST not change during the lifetime of the entity.
  // For example: "service" or "host". This field is required and MUST not be empty
  // for valid entities.
  string type = 2;

  // Attribute Keys that identify the entity.
  // MUST not change during the lifetime of the entity. The Id must contain at least one attribute.
  // These keys MUST exist in the containing {message}.attributes.
  repeated string id_keys = 3;

  // Descriptive (non-identifying) attribute keys of the entity.
  // MAY change over the lifetime of the entity. MAY be empty.
  // These attribute keys are not part of entity's identity.
  // These keys MUST exist in the containing {message}.attributes.
  repeated string description_keys = 4;
}
```

Create `src/LogsPlatform.Web/Otlp/opentelemetry/proto/resource/v1/resource.proto`:

```protobuf
// Copyright 2019, OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

syntax = "proto3";

package opentelemetry.proto.resource.v1;

import "opentelemetry/proto/common/v1/common.proto";

option csharp_namespace = "OpenTelemetry.Proto.Resource.V1";
option java_multiple_files = true;
option java_package = "io.opentelemetry.proto.resource.v1";
option java_outer_classname = "ResourceProto";
option go_package = "go.opentelemetry.io/proto/otlp/resource/v1";

// Resource information.
message Resource {
  // Set of attributes that describe the resource.
  // Attribute keys MUST be unique (it is not allowed to have more than one
  // attribute with the same key).
  // The behavior of software that receives duplicated keys can be unpredictable.
  repeated opentelemetry.proto.common.v1.KeyValue attributes = 1;

  // The number of dropped attributes. If the value is 0, then
  // no attributes were dropped.
  uint32 dropped_attributes_count = 2;

  // Set of entities that participate in this Resource.
  //
  // Note: keys in the references MUST exist in attributes of this message.
  //
  // Status: [Development]
  repeated opentelemetry.proto.common.v1.EntityRef entity_refs = 3;
}
```

Create `src/LogsPlatform.Web/Otlp/opentelemetry/proto/logs/v1/logs.proto`:

```protobuf
// Copyright 2020, OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

syntax = "proto3";

package opentelemetry.proto.logs.v1;

import "opentelemetry/proto/common/v1/common.proto";
import "opentelemetry/proto/resource/v1/resource.proto";

option csharp_namespace = "OpenTelemetry.Proto.Logs.V1";
option java_multiple_files = true;
option java_package = "io.opentelemetry.proto.logs.v1";
option java_outer_classname = "LogsProto";
option go_package = "go.opentelemetry.io/proto/otlp/logs/v1";

// LogsData represents the logs data that can be stored in a persistent storage,
// OR can be embedded by other protocols that transfer OTLP logs data but do not
// implement the OTLP protocol.
//
// The main difference between this message and collector protocol is that
// in this message there will not be any "control" or "metadata" specific to
// OTLP protocol.
//
// When new fields are added into this message, the OTLP request MUST be updated
// as well.
message LogsData {
  // An array of ResourceLogs.
  // For data coming from a single resource this array will typically contain
  // one element. Intermediary nodes that receive data from multiple origins
  // typically batch the data before forwarding further and in that case this
  // array will contain multiple elements.
  repeated ResourceLogs resource_logs = 1;
}

// A collection of ScopeLogs from a Resource.
message ResourceLogs {
  reserved 1000;

  // The resource for the logs in this message.
  // If this field is not set then resource info is unknown.
  opentelemetry.proto.resource.v1.Resource resource = 1;

  // A list of ScopeLogs that originate from a resource.
  repeated ScopeLogs scope_logs = 2;

  // The Schema URL, if known. This is the identifier of the Schema that the resource data
  // is recorded in. Notably, the last part of the URL path is the version number of the
  // schema: http[s]://server[:port]/path/<version>. To learn more about Schema URL see
  // https://opentelemetry.io/docs/specs/otel/schemas/#schema-url
  // This schema_url applies to the data in the "resource" field. It does not apply
  // to the data in the "scope_logs" field which have their own schema_url field.
  string schema_url = 3;
}

// A collection of Logs produced by a Scope.
message ScopeLogs {
  // The instrumentation scope information for the logs in this message.
  // Semantically when InstrumentationScope isn't set, it is equivalent with
  // an empty instrumentation scope name (unknown).
  opentelemetry.proto.common.v1.InstrumentationScope scope = 1;

  // A list of log records.
  repeated LogRecord log_records = 2;

  // The Schema URL, if known. This is the identifier of the Schema that the log data
  // is recorded in. Notably, the last part of the URL path is the version number of the
  // schema: http[s]://server[:port]/path/<version>. To learn more about Schema URL see
  // https://opentelemetry.io/docs/specs/otel/schemas/#schema-url
  // This schema_url applies to the data in the "scope" field and all logs in the
  // "log_records" field.
  string schema_url = 3;
}

// Possible values for LogRecord.SeverityNumber.
enum SeverityNumber {
  SEVERITY_NUMBER_UNSPECIFIED = 0;
  SEVERITY_NUMBER_TRACE  = 1;
  SEVERITY_NUMBER_TRACE2 = 2;
  SEVERITY_NUMBER_TRACE3 = 3;
  SEVERITY_NUMBER_TRACE4 = 4;
  SEVERITY_NUMBER_DEBUG  = 5;
  SEVERITY_NUMBER_DEBUG2 = 6;
  SEVERITY_NUMBER_DEBUG3 = 7;
  SEVERITY_NUMBER_DEBUG4 = 8;
  SEVERITY_NUMBER_INFO   = 9;
  SEVERITY_NUMBER_INFO2  = 10;
  SEVERITY_NUMBER_INFO3  = 11;
  SEVERITY_NUMBER_INFO4  = 12;
  SEVERITY_NUMBER_WARN   = 13;
  SEVERITY_NUMBER_WARN2  = 14;
  SEVERITY_NUMBER_WARN3  = 15;
  SEVERITY_NUMBER_WARN4  = 16;
  SEVERITY_NUMBER_ERROR  = 17;
  SEVERITY_NUMBER_ERROR2 = 18;
  SEVERITY_NUMBER_ERROR3 = 19;
  SEVERITY_NUMBER_ERROR4 = 20;
  SEVERITY_NUMBER_FATAL  = 21;
  SEVERITY_NUMBER_FATAL2 = 22;
  SEVERITY_NUMBER_FATAL3 = 23;
  SEVERITY_NUMBER_FATAL4 = 24;
}

// LogRecordFlags represents constants used to interpret the
// LogRecord.flags field, which is protobuf 'fixed32' type and is to
// be used as bit-fields. Each non-zero value defined in this enum is
// a bit-mask.  To extract the bit-field, for example, use an
// expression like:
//
//   (logRecord.flags & LOG_RECORD_FLAGS_TRACE_FLAGS_MASK)
//
enum LogRecordFlags {
  // The zero value for the enum. Should not be used for comparisons.
  // Instead use bitwise "and" with the appropriate mask as shown above.
  LOG_RECORD_FLAGS_DO_NOT_USE = 0;

  // Bits 0-7 are used for trace flags.
  LOG_RECORD_FLAGS_TRACE_FLAGS_MASK = 0x000000FF;

  // Bits 8-31 are reserved for future use.
}

// A log record according to OpenTelemetry Log Data Model:
// https://github.com/open-telemetry/oteps/blob/main/text/logs/0097-log-data-model.md
message LogRecord {
  reserved 4;

  // time_unix_nano is the time when the event occurred.
  // Value is UNIX Epoch time in nanoseconds since 00:00:00 UTC on 1 January 1970.
  // Value of 0 indicates unknown or missing timestamp.
  fixed64 time_unix_nano = 1;

  // Time when the event was observed by the collection system.
  // For events that originate in OpenTelemetry (e.g. using OpenTelemetry Logging SDK)
  // this timestamp is typically set at the generation time and is equal to Timestamp.
  // For events originating externally and collected by OpenTelemetry (e.g. using
  // Collector) this is the time when OpenTelemetry's code observed the event measured
  // by the clock of the OpenTelemetry code. This field MUST be set once the event is
  // observed by OpenTelemetry.
  //
  // For converting OpenTelemetry log data to formats that support only one timestamp or
  // when receiving OpenTelemetry log data by recipients that support only one timestamp
  // internally the following logic is recommended:
  //   - Use time_unix_nano if it is present, otherwise use observed_time_unix_nano.
  //
  // Value is UNIX Epoch time in nanoseconds since 00:00:00 UTC on 1 January 1970.
  // Value of 0 indicates unknown or missing timestamp.
  fixed64 observed_time_unix_nano = 11;

  // Numerical value of the severity, normalized to values described in Log Data Model.
  // [Optional].
  SeverityNumber severity_number = 2;

  // The severity text (also known as log level). The original string representation as
  // it is known at the source. [Optional].
  string severity_text = 3;

  // A value containing the body of the log record. Can be for example a human-readable
  // string message (including multi-line) describing the event in a free form or it can
  // be a structured data composed of arrays and maps of other values. [Optional].
  opentelemetry.proto.common.v1.AnyValue body = 5;

  // Additional attributes that describe the specific event occurrence. [Optional].
  // Attribute keys MUST be unique (it is not allowed to have more than one
  // attribute with the same key).
  // The behavior of software that receives duplicated keys can be unpredictable.
  repeated opentelemetry.proto.common.v1.KeyValue attributes = 6;
  uint32 dropped_attributes_count = 7;

  // Flags, a bit field. 8 least significant bits are the trace flags as
  // defined in W3C Trace Context specification. 24 most significant bits are reserved
  // and must be set to 0. Readers must not assume that 24 most significant bits
  // will be zero and must correctly mask the bits when reading 8-bit trace flag (use
  // flags & LOG_RECORD_FLAGS_TRACE_FLAGS_MASK). [Optional].
  fixed32 flags = 8;

  // A unique identifier for a trace. All logs from the same trace share
  // the same `trace_id`. The ID is a 16-byte array. An ID with all zeroes OR
  // of length other than 16 bytes is considered invalid (empty string in OTLP/JSON
  // is zero-length and thus is also invalid).
  //
  // This field is optional.
  //
  // The receivers SHOULD assume that the log record is not associated with a
  // trace if any of the following is true:
  //   - the field is not present,
  //   - the field contains an invalid value.
  bytes trace_id = 9;

  // A unique identifier for a span within a trace, assigned when the span
  // is created. The ID is an 8-byte array. An ID with all zeroes OR of length
  // other than 8 bytes is considered invalid (empty string in OTLP/JSON
  // is zero-length and thus is also invalid).
  //
  // This field is optional. If the sender specifies a valid span_id then it SHOULD also
  // specify a valid trace_id.
  //
  // The receivers SHOULD assume that the log record is not associated with a
  // span if any of the following is true:
  //   - the field is not present,
  //   - the field contains an invalid value.
  bytes span_id = 10;

  // A unique identifier of event category/type.
  // All events with the same event_name are expected to conform to the same
  // schema for both their attributes and their body.
  //
  // Recommended to be fully qualified and short (no longer than 256 characters).
  //
  // Presence of event_name on the log record identifies this record
  // as an event.
  //
  // [Optional].
  string event_name = 12;
}
```

Create `src/LogsPlatform.Web/Otlp/opentelemetry/proto/collector/logs/v1/logs_service.proto`:

```protobuf
// Copyright 2020, OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

syntax = "proto3";

package opentelemetry.proto.collector.logs.v1;

import "opentelemetry/proto/logs/v1/logs.proto";

option csharp_namespace = "OpenTelemetry.Proto.Collector.Logs.V1";
option java_multiple_files = true;
option java_package = "io.opentelemetry.proto.collector.logs.v1";
option java_outer_classname = "LogsServiceProto";
option go_package = "go.opentelemetry.io/proto/otlp/collector/logs/v1";

// Service that can be used to push logs between one Application instrumented with
// OpenTelemetry and an collector, or between an collector and a central collector (in this
// case logs are sent/received to/from multiple Applications).
service LogsService {
  rpc Export(ExportLogsServiceRequest) returns (ExportLogsServiceResponse) {}
}

message ExportLogsServiceRequest {
  // An array of ResourceLogs.
  // For data coming from a single resource this array will typically contain one
  // element. Intermediary nodes (such as OpenTelemetry Collector) that receive
  // data from multiple origins typically batch the data before forwarding further and
  // in that case this array will contain multiple elements.
  repeated opentelemetry.proto.logs.v1.ResourceLogs resource_logs = 1;
}

message ExportLogsServiceResponse {
  // The details of a partially successful export request.
  //
  // If the request is only partially accepted
  // (i.e. when the server accepts only parts of the data and rejects the rest)
  // the server MUST initialize the `partial_success` field and MUST
  // set the `rejected_<signal>` with the number of items it rejected.
  //
  // Servers MAY also make use of the `partial_success` field to convey
  // warnings/suggestions to senders even when the request was fully accepted.
  // In such cases, the `rejected_<signal>` MUST have a value of `0` and
  // the `error_message` MUST be non-empty.
  //
  // A `partial_success` message with an empty value (rejected_<signal> = 0 and
  // `error_message` = "") is equivalent to it not being set/present. Senders
  // SHOULD interpret it the same way as in the full success case.
  ExportLogsPartialSuccess partial_success = 1;
}

message ExportLogsPartialSuccess {
  // The number of rejected log records.
  //
  // A `rejected_<signal>` field holding a `0` value indicates that the
  // request was fully accepted.
  int64 rejected_log_records = 1;

  // A developer-facing human-readable message in English. It should be used
  // either to explain why the server rejected parts of the data during a partial
  // success or to convey warnings/suggestions during a full success. The message
  // should offer guidance on how users can address such issues.
  //
  // error_message is an optional field. An error_message with an empty value
  // is equivalent to it not being set.
  string error_message = 2;
}
```

- [ ] **Step 4: Wire Protobuf compilation into `LogsPlatform.Web.csproj`**

Modify `src/LogsPlatform.Web/LogsPlatform.Web.csproj` — add to the existing `<ItemGroup>` containing `PackageReference`s, and add a new `<ItemGroup>` for the `Protobuf` items:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.11" />
    <PackageReference Include="Swashbuckle.AspNetCore" Version="10.2.3" />
    <PackageReference Include="Google.Protobuf" Version="3.36.0" />
    <PackageReference Include="Grpc.Tools" Version="2.83.0" PrivateAssets="All" />
  </ItemGroup>

  <ItemGroup>
    <Protobuf Include="Otlp/opentelemetry/proto/common/v1/common.proto" ProtoRoot="Otlp" GrpcServices="None" />
    <Protobuf Include="Otlp/opentelemetry/proto/resource/v1/resource.proto" ProtoRoot="Otlp" GrpcServices="None" />
    <Protobuf Include="Otlp/opentelemetry/proto/logs/v1/logs.proto" ProtoRoot="Otlp" GrpcServices="None" />
    <Protobuf Include="Otlp/opentelemetry/proto/collector/logs/v1/logs_service.proto" ProtoRoot="Otlp" GrpcServices="None" />
  </ItemGroup>
```

`GrpcServices="None"` generates only the message classes (`ExportLogsServiceRequest`/`ExportLogsServiceResponse`/etc.) — not a gRPC service stub, which would need the gRPC runtime and is explicitly out of scope (Protobuf/HTTP only, not gRPC transport).

- [ ] **Step 5: Add matching package references to the test project**

Modify `tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj` — add to the existing `<ItemGroup>` containing `PackageReference`s:

```xml
    <PackageReference Include="Google.Protobuf" Version="3.36.0" />
```

(`Grpc.Tools` is not needed here — the test project doesn't compile any `.proto` files itself; it consumes the already-generated public classes from `LogsPlatform.Web` via the existing `ProjectReference`.)

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test --filter OtlpGeneratedTypesTests`
Expected: PASS (1 test, 0 failures) — the generated `OpenTelemetry.Proto.*` classes are public and round-trip correctly through bytes.

- [ ] **Step 7: Commit**

```bash
git add src/LogsPlatform.Web/Otlp src/LogsPlatform.Web/LogsPlatform.Web.csproj tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj tests/LogsPlatform.Tests/Web/OtlpGeneratedTypesTests.cs
git commit -m "feat: vendor OTLP proto files and wire Protobuf codegen"
```

---

## Task 2: SeverityLevels OTel severity-number mapping

**Files:**
- Modify: `src/LogsPlatform.Web/Services/SeverityLevels.cs`
- Test: `tests/LogsPlatform.Tests/Web/SeverityLevelsTests.cs` (existing file, add cases)

**Interfaces:**
- Consumes: `SeverityLevels.ByValue` (existing, `IReadOnlyDictionary<int, string>`, keys `1, 5, 9, 13, 17, 21`).
- Produces: `SeverityLevels.FromOtelSeverityNumber(int severityNumber) : string?` — used by Task 3's `OtlpLogMapper`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/LogsPlatform.Tests/Web/SeverityLevelsTests.cs` (inside the existing `SeverityLevelsTests` class, after `ByValue_IsExactReverseOfByName`):

```csharp
    [Theory]
    [InlineData(1, "Trace")]
    [InlineData(2, "Trace")]
    [InlineData(4, "Trace")]
    [InlineData(5, "Debug")]
    [InlineData(8, "Debug")]
    [InlineData(9, "Info")]
    [InlineData(12, "Info")]
    [InlineData(13, "Warn")]
    [InlineData(16, "Warn")]
    [InlineData(17, "Error")]
    [InlineData(20, "Error")]
    [InlineData(21, "Fatal")]
    [InlineData(24, "Fatal")]
    public void FromOtelSeverityNumber_ValueInBand_ReturnsBandName(int severityNumber, string expected)
    {
        Assert.Equal(expected, SeverityLevels.FromOtelSeverityNumber(severityNumber));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(-1)]
    public void FromOtelSeverityNumber_OutOfRange_ReturnsNull(int severityNumber)
    {
        Assert.Null(SeverityLevels.FromOtelSeverityNumber(severityNumber));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter SeverityLevelsTests`
Expected: build error — `'SeverityLevels' does not contain a definition for 'FromOtelSeverityNumber'`.

- [ ] **Step 3: Implement the mapping**

Modify `src/LogsPlatform.Web/Services/SeverityLevels.cs`:

```csharp
namespace LogsPlatform.Web.Services;

public static class SeverityLevels
{
    public static readonly IReadOnlyDictionary<string, int> ByName = new Dictionary<string, int>
    {
        ["Trace"] = 1, ["Debug"] = 5, ["Info"] = 9, ["Warn"] = 13, ["Error"] = 17, ["Fatal"] = 21
    };

    public static readonly IReadOnlyDictionary<int, string> ByValue =
        ByName.ToDictionary(pair => pair.Value, pair => pair.Key);

    // OTel SeverityNumber is 1-24 in 4-wide bands (TRACE=1-4, DEBUG=5-8, ..., FATAL=21-24);
    // this project's own severity values already align to each band's first number.
    public static string? FromOtelSeverityNumber(int severityNumber)
    {
        if (severityNumber < 1 || severityNumber > 24)
        {
            return null;
        }
        var band = ((severityNumber - 1) / 4) * 4 + 1;
        return ByValue[band];
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter SeverityLevelsTests`
Expected: PASS (all `SeverityLevelsTests`, including the new theories).

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Web/Services/SeverityLevels.cs tests/LogsPlatform.Tests/Web/SeverityLevelsTests.cs
git commit -m "feat: add OTel SeverityNumber to internal Severity mapping"
```

---

## Task 3: OtlpLogMapper — LogRecord to IngestEventRequest

**Files:**
- Create: `src/LogsPlatform.Web/Services/OtlpLogMapper.cs`
- Test: `tests/LogsPlatform.Tests/Web/OtlpLogMapperTests.cs`

**Interfaces:**
- Consumes: `OpenTelemetry.Proto.Logs.V1.LogRecord`, `OpenTelemetry.Proto.Resource.V1.Resource` (Task 1), `SeverityLevels.FromOtelSeverityNumber` (Task 2), `LogsPlatform.Web.Contracts.IngestEventRequest`/`IngestHierarchyRequest`/`IngestExceptionRequest` (existing, `src/LogsPlatform.Web/Contracts/IngestionContracts.cs`).
- Produces: `OtlpLogMapper.Map(LogRecord record, Resource? resource) : IngestEventRequest` — used by Task 4's `OtlpLogsController`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LogsPlatform.Tests/Web/OtlpLogMapperTests.cs`:

```csharp
using System.Text.Json;
using Google.Protobuf;
using LogsPlatform.Web.Services;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using Xunit;

namespace LogsPlatform.Tests.Web;

public class OtlpLogMapperTests
{
    private static KeyValue Attr(string key, string value) =>
        new() { Key = key, Value = new AnyValue { StringValue = value } };

    [Fact]
    public void Map_TimeUnixNano_ConvertsToUtcDateTime()
    {
        var expected = new DateTime(2026, 8, 24, 12, 30, 0, DateTimeKind.Utc);
        var nanosSinceEpoch = (ulong)(expected - DateTime.UnixEpoch).Ticks * 100;
        var record = new LogRecord { TimeUnixNano = nanosSinceEpoch, Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal(expected, result.Timestamp);
    }

    [Fact]
    public void Map_ZeroTimeUnixNano_TimestampIsNull()
    {
        var record = new LogRecord { TimeUnixNano = 0, Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Null(result.Timestamp);
    }

    [Theory]
    [InlineData(SeverityNumber.Trace, "Trace")]
    [InlineData(SeverityNumber.Debug, "Debug")]
    [InlineData(SeverityNumber.Info, "Info")]
    [InlineData(SeverityNumber.Warn, "Warn")]
    [InlineData(SeverityNumber.Error, "Error")]
    [InlineData(SeverityNumber.Fatal, "Fatal")]
    public void Map_SeverityNumber_MapsToExpectedName(SeverityNumber severity, string expected)
    {
        var record = new LogRecord { SeverityNumber = severity, Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal(expected, result.Severity);
    }

    [Fact]
    public void Map_StringBody_UsesStringDirectly()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "something failed" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal("something failed", result.Message);
    }

    [Fact]
    public void Map_NonStringBody_SerializesToJson()
    {
        var record = new LogRecord { Body = new AnyValue { IntValue = 42 } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal("42", result.Message);
    }

    [Fact]
    public void Map_TraceIdAndSpanId_ConvertToLowercaseHex()
    {
        var record = new LogRecord
        {
            Body = new AnyValue { StringValue = "m" },
            TraceId = ByteString.CopyFrom(new byte[] { 0xAB, 0xCD, 0x01 }),
            SpanId = ByteString.CopyFrom(new byte[] { 0xEF, 0x02 })
        };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal("abcd01", result.TraceId);
        Assert.Equal("ef02", result.SpanId);
    }

    [Fact]
    public void Map_EmptyTraceId_IsNull()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Null(result.TraceId);
        Assert.Null(result.SpanId);
    }

    [Fact]
    public void Map_DeploymentEnvironmentResourceAttribute_MapsToEnvironment()
    {
        var resource = new Resource();
        resource.Attributes.Add(Attr("deployment.environment", "Production"));
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, resource);

        Assert.Equal("Production", result.Environment);
    }

    [Fact]
    public void Map_ServiceNameResourceAttribute_DoesNotAffectMapping()
    {
        var resource = new Resource();
        resource.Attributes.Add(Attr("service.name", "SomeService"));
        resource.Attributes.Add(Attr("deployment.environment", "Production"));
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, resource);

        Assert.Equal("Production", result.Environment);
        Assert.True(result.Metadata is null || !result.Metadata.ContainsKey("service.name"));
    }

    [Fact]
    public void Map_ExceptionAttributes_MapToExceptionField()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };
        record.Attributes.Add(Attr("exception.type", "System.TimeoutException"));
        record.Attributes.Add(Attr("exception.stacktrace", "at Foo.Bar()"));

        var result = OtlpLogMapper.Map(record, null);

        Assert.NotNull(result.Exception);
        Assert.Equal("System.TimeoutException", result.Exception!.Type);
        Assert.Equal("at Foo.Bar()", result.Exception.StackTrace);
    }

    [Fact]
    public void Map_LogsPlatformHierarchyAttributes_MapToHierarchy()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };
        record.Attributes.Add(Attr("logsplatform.module", "Payments"));
        record.Attributes.Add(Attr("logsplatform.screen_service", "Checkout"));
        record.Attributes.Add(Attr("logsplatform.process", "ChargeCard"));
        record.Attributes.Add(Attr("logsplatform.operation", "Authorize"));

        var result = OtlpLogMapper.Map(record, null);

        Assert.NotNull(result.Hierarchy);
        Assert.Equal("Payments", result.Hierarchy!.Module);
        Assert.Equal("Checkout", result.Hierarchy.ScreenService);
        Assert.Equal("ChargeCard", result.Hierarchy.Process);
        Assert.Equal("Authorize", result.Hierarchy.Operation);
    }

    [Fact]
    public void Map_LogsPlatformCustomerAndUserAttributes_MapDirectly()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };
        record.Attributes.Add(Attr("logsplatform.customer_id", "cust-1"));
        record.Attributes.Add(Attr("logsplatform.user_id", "user-1"));

        var result = OtlpLogMapper.Map(record, null);

        Assert.Equal("cust-1", result.CustomerId);
        Assert.Equal("user-1", result.UserId);
    }

    [Fact]
    public void Map_UnmappedAttributes_GoToMetadata()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };
        record.Attributes.Add(Attr("http.method", "POST"));

        var result = OtlpLogMapper.Map(record, null);

        Assert.NotNull(result.Metadata);
        Assert.Equal("POST", result.Metadata!["http.method"]);
    }

    [Fact]
    public void Map_NoAttributes_MetadataIsNull()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Null(result.Metadata);
    }

    [Fact]
    public void Map_NullResource_EnvironmentIsNull()
    {
        var record = new LogRecord { Body = new AnyValue { StringValue = "m" } };

        var result = OtlpLogMapper.Map(record, null);

        Assert.Null(result.Environment);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter OtlpLogMapperTests`
Expected: build error — `'OtlpLogMapper' does not exist`.

- [ ] **Step 3: Implement OtlpLogMapper**

Create `src/LogsPlatform.Web/Services/OtlpLogMapper.cs`:

```csharp
using System.Text.Json;
using LogsPlatform.Web.Contracts;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace LogsPlatform.Web.Services;

public static class OtlpLogMapper
{
    private static readonly HashSet<string> MappedAttributeKeys = new()
    {
        "logsplatform.module", "logsplatform.screen_service", "logsplatform.process", "logsplatform.operation",
        "logsplatform.customer_id", "logsplatform.user_id", "exception.type", "exception.stacktrace"
    };

    public static IngestEventRequest Map(LogRecord record, Resource? resource)
    {
        var moduleAttr = FindStringAttribute(record.Attributes, "logsplatform.module");
        var screenServiceAttr = FindStringAttribute(record.Attributes, "logsplatform.screen_service");
        var processAttr = FindStringAttribute(record.Attributes, "logsplatform.process");
        var operationAttr = FindStringAttribute(record.Attributes, "logsplatform.operation");
        IngestHierarchyRequest? hierarchy = moduleAttr is null && screenServiceAttr is null && processAttr is null && operationAttr is null
            ? null
            : new IngestHierarchyRequest(moduleAttr, screenServiceAttr, processAttr, operationAttr);

        var exceptionType = FindStringAttribute(record.Attributes, "exception.type");
        IngestExceptionRequest? exception = exceptionType is null
            ? null
            : new IngestExceptionRequest(exceptionType, FindStringAttribute(record.Attributes, "exception.stacktrace"));

        Dictionary<string, object>? metadata = null;
        foreach (var attribute in record.Attributes)
        {
            if (MappedAttributeKeys.Contains(attribute.Key))
            {
                continue;
            }
            metadata ??= new Dictionary<string, object>();
            metadata[attribute.Key] = attribute.Value is null ? string.Empty : AnyValueToObject(attribute.Value);
        }

        return new IngestEventRequest(
            EventKey: null,
            Timestamp: UnixNanoToDateTime(record.TimeUnixNano),
            Severity: SeverityLevels.FromOtelSeverityNumber((int)record.SeverityNumber),
            // Resource.Attributes["service.name"] is intentionally not read: Application identity
            // comes from the API Key alone, matching every other ingestion path in this project.
            Environment: resource is null ? null : FindStringAttribute(resource.Attributes, "deployment.environment"),
            Version: null,
            Hierarchy: hierarchy,
            CorrelationId: null,
            TraceId: ByteStringToHex(record.TraceId),
            SpanId: ByteStringToHex(record.SpanId),
            ParentSpanId: null,
            DurationMs: null,
            CustomerId: FindStringAttribute(record.Attributes, "logsplatform.customer_id"),
            UserId: FindStringAttribute(record.Attributes, "logsplatform.user_id"),
            Message: AnyValueToMessage(record.Body),
            MessageTemplate: null,
            Exception: exception,
            Metadata: metadata);
    }

    private static DateTime? UnixNanoToDateTime(ulong timeUnixNano)
    {
        if (timeUnixNano == 0)
        {
            return null;
        }
        return DateTime.UnixEpoch.AddTicks((long)(timeUnixNano / 100));
    }

    private static string? ByteStringToHex(Google.Protobuf.ByteString id) =>
        id.Length == 0 ? null : Convert.ToHexString(id.Span).ToLowerInvariant();

    private static string? FindStringAttribute(IEnumerable<KeyValue> attributes, string key)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Key != key)
            {
                continue;
            }
            return attribute.Value?.ValueCase == AnyValue.ValueOneofCase.StringValue ? attribute.Value.StringValue : null;
        }
        return null;
    }

    private static string? AnyValueToMessage(AnyValue? body)
    {
        if (body is null)
        {
            return null;
        }
        return body.ValueCase == AnyValue.ValueOneofCase.StringValue
            ? body.StringValue
            : JsonSerializer.Serialize(AnyValueToObject(body));
    }

    private static object AnyValueToObject(AnyValue value) => value.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
        AnyValue.ValueOneofCase.IntValue => value.IntValue,
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
        AnyValue.ValueOneofCase.BytesValue => Convert.ToHexString(value.BytesValue.Span).ToLowerInvariant(),
        AnyValue.ValueOneofCase.ArrayValue => value.ArrayValue.Values.Select(AnyValueToObject).ToList(),
        AnyValue.ValueOneofCase.KvlistValue => value.KvlistValue.Values.ToDictionary(kv => kv.Key, kv => AnyValueToObject(kv.Value)),
        _ => string.Empty
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter OtlpLogMapperTests`
Expected: PASS (all 15 tests in `OtlpLogMapperTests`).

- [ ] **Step 5: Commit**

```bash
git add src/LogsPlatform.Web/Services/OtlpLogMapper.cs tests/LogsPlatform.Tests/Web/OtlpLogMapperTests.cs
git commit -m "feat: add OtlpLogMapper for LogRecord to IngestEventRequest mapping"
```

---

## Task 4: OtlpLogsController — POST /v1/logs endpoint

**Files:**
- Create: `src/LogsPlatform.Web/Controllers/OtlpLogsController.cs`
- Test: `tests/LogsPlatform.Tests/Web/OtlpLogsControllerTests.cs`

**Interfaces:**
- Consumes: `OtlpLogMapper.Map` (Task 3), `IngestionProcessor.ProcessAsync(int applicationId, IngestEventRequest request) : Task<ProcessedEvent>` (existing, `src/LogsPlatform.Web/Services/IngestionProcessor.cs`), `IEventRepository.AddEventsAsync(int applicationId, IReadOnlyList<Event> events)` (existing), `ApiKeyAuthenticationOptions.SchemeName`/`ApiKeyAuthenticationHandler.ApplicationIdClaimType` (existing, `src/LogsPlatform.Web/Authentication/`).
- Produces: `POST /v1/logs` HTTP endpoint, Protobuf request/response, used directly by external OTLP clients and by Task 5's real-SDK-client test.

- [ ] **Step 1: Write the failing tests**

Create `tests/LogsPlatform.Tests/Web/OtlpLogsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Google.Protobuf;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class OtlpLogsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public OtlpLogsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<(int ApplicationId, int EnvironmentId, string ApiKey)> CreateAppWithApiKeyAsync(HttpClient client, string appName)
    {
        var appResponse = await client.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest(appName, null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();

        var envResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();

        var keyResponse = await client.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("OTLP test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        return (app.Id, env!.Id, key!.ApiKey);
    }

    private static ExportLogsServiceRequest ValidRequest(string message = "otlp event")
    {
        var logRecord = new LogRecord
        {
            TimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
            SeverityNumber = SeverityNumber.Error,
            Body = new AnyValue { StringValue = message }
        };
        var resource = new Resource();
        resource.Attributes.Add(new KeyValue { Key = "deployment.environment", Value = new AnyValue { StringValue = "Production" } });

        var scopeLogs = new ScopeLogs();
        scopeLogs.LogRecords.Add(logRecord);
        var resourceLogs = new ResourceLogs { Resource = resource };
        resourceLogs.ScopeLogs.Add(scopeLogs);

        var request = new ExportLogsServiceRequest();
        request.ResourceLogs.Add(resourceLogs);
        return request;
    }

    private static HttpRequestMessage BuildRequest(string? apiKey, ExportLogsServiceRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/logs")
        {
            Content = new ByteArrayContent(body.ToByteArray())
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        if (apiKey is not null)
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }
        return request;
    }

    [Fact]
    public async Task Export_ValidLogRecord_Returns200AndPersistsEvent()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (applicationId, environmentId, apiKey) = await CreateAppWithApiKeyAsync(client, "OtlpValidTestApp");

        var response = await client.SendAsync(BuildRequest(apiKey, ValidRequest()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        var parsed = ExportLogsServiceResponse.Parser.ParseFrom(responseBytes);
        Assert.False(parsed.HasPartialSuccess);

        var eventsResponse = await client.GetAsync($"/api/v1/events?applicationId={applicationId}&environmentId={environmentId}&page=1&pageSize=10");
        var events = await eventsResponse.Content.ReadFromJsonAsync<EventListResponse>();
        Assert.Equal(1, events!.TotalCount);
        Assert.Equal("otlp event", events.Items[0].Message);
    }

    [Fact]
    public async Task Export_MissingApiKey_Returns401()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.SendAsync(BuildRequest(null, ValidRequest()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Export_InvalidApiKey_Returns401()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);

        var response = await client.SendAsync(BuildRequest("lgp_not-a-real-key", ValidRequest()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Export_UnmappableSeverity_ReportsPartialSuccessWithRejectedCount()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (_, _, apiKey) = await CreateAppWithApiKeyAsync(client, "OtlpRejectTestApp");
        var badRequest = ValidRequest();
        badRequest.ResourceLogs[0].ScopeLogs[0].LogRecords[0].SeverityNumber = SeverityNumber.Unspecified;

        var response = await client.SendAsync(BuildRequest(apiKey, badRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        var parsed = ExportLogsServiceResponse.Parser.ParseFrom(responseBytes);
        Assert.True(parsed.HasPartialSuccess);
        Assert.Equal(1, parsed.PartialSuccess.RejectedLogRecords);
    }

    [Fact]
    public async Task Export_TwoLogRecordsOneInvalid_AcceptsValidRejectsInvalid()
    {
        var client = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var (applicationId, environmentId, apiKey) = await CreateAppWithApiKeyAsync(client, "OtlpPartialBatchTestApp");
        var request = ValidRequest("good event");
        var badRecord = new LogRecord
        {
            TimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000,
            SeverityNumber = SeverityNumber.Unspecified,
            Body = new AnyValue { StringValue = "bad event" }
        };
        request.ResourceLogs[0].ScopeLogs[0].LogRecords.Add(badRecord);

        var response = await client.SendAsync(BuildRequest(apiKey, request));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        var parsed = ExportLogsServiceResponse.Parser.ParseFrom(responseBytes);
        Assert.Equal(1, parsed.PartialSuccess.RejectedLogRecords);

        var eventsResponse = await client.GetAsync($"/api/v1/events?applicationId={applicationId}&environmentId={environmentId}&page=1&pageSize=10");
        var events = await eventsResponse.Content.ReadFromJsonAsync<EventListResponse>();
        Assert.Equal(1, events!.TotalCount);
        Assert.Equal("good event", events.Items[0].Message);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter OtlpLogsControllerTests`
Expected: all requests return 404 Not Found (route doesn't exist yet) — assertions on `HttpStatusCode.OK`/`Unauthorized` fail.

- [ ] **Step 3: Implement OtlpLogsController**

Create `src/LogsPlatform.Web/Controllers/OtlpLogsController.cs`:

```csharp
using System.Security.Claims;
using Google.Protobuf;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Authentication;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("v1/logs")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public class OtlpLogsController : ControllerBase
{
    private const int DefaultRateLimitPerMinute = 1000;
    private const string ProtobufContentType = "application/x-protobuf";

    private readonly IngestionProcessor _processor;
    private readonly IEventRepository _events;
    private readonly IMemoryCache _cache;
    private readonly int _rateLimitPerMinute;

    public OtlpLogsController(IngestionProcessor processor, IEventRepository events, IMemoryCache cache, IConfiguration configuration)
    {
        _processor = processor;
        _events = events;
        _cache = cache;
        _rateLimitPerMinute = configuration.GetValue("Ingestion:RateLimitPerMinute", DefaultRateLimitPerMinute);
    }

    [HttpPost]
    [Consumes(ProtobufContentType)]
    public async Task<IActionResult> Export()
    {
        var applicationId = int.Parse(User.FindFirstValue(ApiKeyAuthenticationHandler.ApplicationIdClaimType)!);

        // Kestrel disallows synchronous reads on Request.Body; MessageParser.ParseFrom(Stream)
        // reads synchronously, so the body must be buffered into a seekable MemoryStream first.
        ExportLogsServiceRequest request;
        using (var buffer = new MemoryStream())
        {
            await Request.Body.CopyToAsync(buffer);
            buffer.Position = 0;
            request = ExportLogsServiceRequest.Parser.ParseFrom(buffer);
        }

        var recordCount = request.ResourceLogs.Sum(rl => rl.ScopeLogs.Sum(sl => sl.LogRecords.Count));
        var counter = _cache.GetOrCreate($"ingest-rate:{applicationId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return new RateCounter();
        })!;
        if (Interlocked.Add(ref counter.Count, Math.Max(recordCount, 1)) > _rateLimitPerMinute)
        {
            Response.Headers["Retry-After"] = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var rejectedReasons = new List<string>();
        var toInsert = new List<Event>();

        foreach (var resourceLogs in request.ResourceLogs)
        {
            foreach (var scopeLogs in resourceLogs.ScopeLogs)
            {
                foreach (var logRecord in scopeLogs.LogRecords)
                {
                    var mapped = OtlpLogMapper.Map(logRecord, resourceLogs.Resource);
                    var processed = await _processor.ProcessAsync(applicationId, mapped);
                    if (processed.RejectReason is not null)
                    {
                        rejectedReasons.Add(processed.RejectReason);
                        continue;
                    }
                    toInsert.Add(processed.Event!);
                }
            }
        }

        await _events.AddEventsAsync(applicationId, toInsert);

        var response = new ExportLogsServiceResponse();
        if (rejectedReasons.Count > 0)
        {
            response.PartialSuccess = new ExportLogsPartialSuccess
            {
                RejectedLogRecords = rejectedReasons.Count,
                ErrorMessage = string.Join("; ", rejectedReasons)
            };
        }

        return File(response.ToByteArray(), ProtobufContentType);
    }

    private class RateCounter
    {
        public int Count;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter OtlpLogsControllerTests`
Expected: PASS (all 5 tests in `OtlpLogsControllerTests`).

- [ ] **Step 5: Run the full existing ingestion test suite to confirm no regression**

Run: `dotnet test --filter "FullyQualifiedName~IngestionControllerTests|FullyQualifiedName~IngestionProcessorTests"`
Expected: PASS, unchanged — the existing `POST /api/v1/ingest/events` endpoint and `IngestionProcessor` were not modified.

- [ ] **Step 6: Commit**

```bash
git add src/LogsPlatform.Web/Controllers/OtlpLogsController.cs tests/LogsPlatform.Tests/Web/OtlpLogsControllerTests.cs
git commit -m "feat: add POST /v1/logs OTLP ingestion endpoint"
```

---

## Task 5: Real OTel .NET SDK client validation

**Files:**
- Modify: `tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj`
- Test: `tests/LogsPlatform.Tests/Web/OtlpRealClientTests.cs`

**Interfaces:**
- Consumes: `POST /v1/logs` (Task 4), `TestWebApplicationFactory`/`AuthenticatedTestClientHelper` (existing, `tests/LogsPlatform.Tests/Web/TestWebApplicationFactory.cs` / `tests/LogsPlatform.Tests/Infrastructure/AuthenticatedTestClientHelper.cs`), `EventListResponse` (existing, `src/LogsPlatform.Web/Contracts/QueryContracts.cs`).
- Produces: nothing consumed by later tasks — this is the final validation task per the design doc's testing requirement.

This is the decisive test the design doc requires: a real `OpenTelemetry`/`OpenTelemetry.Exporter.OpenTelemetryProtocol` SDK client, sending a real log through `ILogger`, against the actual endpoint — not a hand-built Protobuf request. It uses `OtlpExporterOptions.HttpClientFactory` to point the exporter's HTTP client at the in-memory `TestServer` from `WebApplicationFactory.CreateClient()`, so no real network listener is needed.

- [ ] **Step 1: Add the OTel SDK client packages to the test project**

Modify `tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj` — add to the existing `<ItemGroup>` containing `PackageReference`s (alongside the `Google.Protobuf` reference added in Task 1):

```xml
    <PackageReference Include="OpenTelemetry" Version="1.18.0" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.18.0" />
```

- [ ] **Step 2: Write the test**

Create `tests/LogsPlatform.Tests/Web/OtlpRealClientTests.cs`:

```csharp
using System.Net.Http.Json;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Contracts;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using Xunit;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class OtlpRealClientTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public OtlpRealClientTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RealOtelSdkClient_SendsLogViaILogger_EventIsPersisted()
    {
        var adminClient = await AuthenticatedTestClientHelper.CreateAuthenticatedClientAsync(_factory);
        var appResponse = await adminClient.PostAsJsonAsync("/api/v1/admin/applications", new CreateApplicationRequest("OtlpRealClientTestApp", null));
        var app = await appResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        var envResponse = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app!.Id}/environments", new CreateEnvironmentRequest("Production", true));
        var env = await envResponse.Content.ReadFromJsonAsync<EnvironmentResponse>();
        var keyResponse = await adminClient.PostAsJsonAsync($"/api/v1/admin/applications/{app.Id}/api-keys", new CreateApiKeyRequest("Real OTel client test key"));
        var key = await keyResponse.Content.ReadFromJsonAsync<CreateApiKeyResponse>();

        var otlpHttpClient = _factory.CreateClient();
        otlpHttpClient.BaseAddress = new Uri("http://localhost/");

        using (var loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddAttributes(new[]
                {
                    new KeyValuePair<string, object>("deployment.environment", "Production")
                }));
                options.AddOtlpExporter(otlp =>
                {
                    otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    otlp.Endpoint = new Uri("http://localhost/v1/logs");
                    otlp.HttpClientFactory = () => otlpHttpClient;
                    otlp.Headers = $"X-Api-Key={key!.ApiKey}";
                    otlp.ExportProcessorType = OpenTelemetry.ExportProcessorType.Simple;
                });
            });
        }))
        {
            var logger = loggerFactory.CreateLogger("OtlpRealClientTests");
            logger.LogError("real otel sdk client test message");
        }

        var eventsResponse = await adminClient.GetAsync($"/api/v1/events?applicationId={app.Id}&environmentId={env!.Id}&page=1&pageSize=10");
        var events = await eventsResponse.Content.ReadFromJsonAsync<EventListResponse>();

        Assert.Equal(1, events!.TotalCount);
        Assert.Equal("real otel sdk client test message", events.Items[0].Message);
        Assert.Equal("Error", events.Items[0].Severity);
    }
}
```

`ExportProcessorType.Simple` makes the exporter send synchronously on each log call instead of batching on a background timer, so the event is guaranteed to have arrived by the time `loggerFactory` is disposed and the assertions run.

- [ ] **Step 3: Run test to verify it fails first for the right reason**

Run: `dotnet test --filter OtlpRealClientTests`
Expected: build error before Task 5's packages are added (`OpenTelemetry`/`OpenTelemetry.Exporter.OpenTelemetryProtocol` types don't exist) — confirms the test genuinely exercises the new package references, not a pre-existing path. (If Step 1 was already done, this step instead shows either a connection failure or `TotalCount == 0`, confirming the test fails before the corresponding controller/mapper code — already present from Tasks 1-4 — is exercised end-to-end for the first time via a real client.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter OtlpRealClientTests`
Expected: PASS. This confirms end-to-end: real OTel SDK → real OTLP/HTTP Protobuf wire format → `POST /v1/logs` → `OtlpLogMapper` → `IngestionProcessor` → persisted `Event`, queryable via the existing `GET /api/v1/events`.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, 0 failures (existing suite was 326/326 before this plan; expect 326 + new tests from Tasks 1-5, all passing).

- [ ] **Step 6: Commit**

```bash
git add tests/LogsPlatform.Tests/LogsPlatform.Tests.csproj tests/LogsPlatform.Tests/Web/OtlpRealClientTests.cs
git commit -m "test: validate OTLP ingestion against a real OpenTelemetry .NET SDK client"
```

---

## Final Verification

After all 5 tasks are complete:

1. `dotnet build` — 0 errors, 0 warnings.
2. `dotnet test` — full suite passes (326 pre-existing + ~25 new tests across the 5 tasks).
3. Confirm `POST /api/v1/ingest/events` (the proprietary endpoint) is byte-for-byte unmodified — `git diff main -- src/LogsPlatform.Web/Controllers/IngestionController.cs src/LogsPlatform.Web/Services/IngestionProcessor.cs` should be empty.
4. Confirm the new endpoint is additive only — `git diff main --stat` should show only new files plus the two `.csproj` modifications (package references) and the two-line `SeverityLevels.cs` addition.
