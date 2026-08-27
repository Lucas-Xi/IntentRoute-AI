# AI Route Decision Simulator design

Status: implementation slice for the v0.3 development preview

## Problem statement

IntentRoute AI users can inspect the generated rule order and run a static policy health check, but they still have to mentally combine process, destination, port, transport, priority, and global fallback semantics to answer a common question: “What would this saved policy do for this hypothetical request?” The answer must be useful without being confused with observed traffic, DNS resolution, proxy reachability, or a running sing-box decision.

## Primary user and job

The primary user is a Windows operator preparing or reviewing a sing-box routing policy. Before applying or changing a rule, they want a deterministic explanation of the first rule that can be proven to win for one concrete hypothetical input.

## Product principles

- The simulator is read-only and works from a cloned configuration snapshot.
- It uses the same canonical runtime order and supported matching semantics as the production sing-box configuration builder.
- It fails closed when the policy is invalid or when the supplied destination context cannot prove a winner.
- It never claims to show a connection, packet, DNS answer, runtime event, proxy probe, or observed route.
- It never runs while configuration recovery protection is active; an empty placeholder must not be presented as the user’s policy.
- A result is bound to both the configuration snapshot and the normalized query so stale results can be rejected.

## Version 1 query contract

The user supplies exactly:

1. one exact process name, without wildcard or regular-expression syntax;
2. one concrete destination type: DNS domain or literal IPv4/IPv6 address;
3. one concrete destination value, without wildcard or CIDR syntax;
4. one port from 1 through 65535; and
5. TCP or UDP.

The simulator does not resolve domains to IP addresses and does not infer a domain from an IP address.

## Decision states

- **Matched rule:** the first enabled rule in canonical runtime order is a proven match.
- **Global fallback:** every enabled rule is a proven miss, so the configured global default is proven to apply.
- **Indeterminate:** an earlier rule could match, but the query lacks resolved-IP or domain context, or the bounded evaluation budget is exhausted.
- **Invalid query:** the hypothetical input is incomplete or outside the version 1 contract.
- **Invalid policy:** the production builder rejects the configuration or the simulator cannot represent a supported rule.

The destination fields inside one sing-box rule form an OR group. Therefore, a matching domain proves the destination group even if that rule also contains CIDRs, and a matching IP proves it even if the rule also contains domains. A domain miss cannot disprove a rule that also contains CIDRs without a resolved IP. An IP miss cannot disprove a rule that also contains domains without domain context. Process, transport, or port mismatches remain sufficient to disprove the rule because these groups combine with the destination group using AND semantics.

## User journey

1. Open **AI 路由推演** from the sidebar.
2. Read the persistent static what-if disclaimer.
3. Enter one exact process, destination, port, and transport.
4. Select **开始本地推演**.
5. See a proven rule, proven global fallback, or an explicitly indeterminate/invalid result.
6. Review the local evaluation trace and optionally locate the matched rule in Rule Management.
7. If the policy changes, the previous result is marked stale and cannot be presented as current.

## Functional requirements

- FR-1: Evaluate enabled rules in `PolicyRuntimeOrder.Enabled` order.
- FR-2: Validate the complete snapshot with `SingBoxConfigBuilder.Build` before evaluating it.
- FR-3: Support exact/global process, exact/suffix domain, IPv4/IPv6 CIDR, port/range, TCP/UDP/Both, Direct/Proxy/Block, explicit/default proxy, and global fallback semantics already emitted by the builder.
- FR-4: Stop at the first proven match or first indeterminate earlier rule.
- FR-5: Bound one run to 500 evaluated rules and honor cancellation.
- FR-6: Show a local trace with evaluation order, rule label, and a reason; never send that trace to an AI provider.
- FR-7: Reject stale results by checking a fingerprint that binds snapshot plus normalized query.
- FR-8: Disable the page during configuration recovery protection.

## Non-functional and security requirements

- NFR-1: No network access, DNS calls, proxy probes, process enumeration, runtime log reads, packet capture, or sing-box process interaction.
- NFR-2: No configuration mutation, persistence, runtime apply, or rule enable/disable action.
- NFR-3: Keep query values, rule IDs, rule labels, proxy IDs, and the trace local.
- NFR-4: Run bounded evaluation off the UI thread and expose cancellation-safe shutdown behavior.
- NFR-5: Use explicit language such as “static what-if” and “not observed traffic” in the interface and documentation.

## Acceptance criteria

- Domain, IP, port, protocol, process, priority, disabled-rule, proxy identity, Direct/Block, and global fallback tests pass.
- Mixed domain/IP rules produce `Indeterminate` when the missing context could change the first winner.
- Known members of a mixed destination OR group remain proven matches.
- Invalid queries and invalid policies never return a route action.
- The configuration serializes identically before and after a simulation.
- A query or configuration change invalidates the result fingerprint.
- Recovery protection prevents simulation against placeholder state.
- `scripts/test.ps1`, `scripts/build.ps1`, package verification, WPF smoke, vulnerability checks, and the pinned real sing-box gate pass.

## Explicitly out of scope

- live connection telemetry or history;
- packet or process interception;
- DNS resolution or reverse lookup;
- proxy reachability, latency, or egress validation;
- automatic policy changes;
- claiming parity with every future sing-box routing feature; and
- sending the hypothetical query or local trace to OpenAI or Ollama.
