# Relay73 RF Lab

Repeatable integration and radio-link testing for the **Relay73** applications, gateway, and modem adapters.

> **Status:** Repository scaffold. Existing FakeRadio, gateway, and Modem73 test work is still being organized for migration.

## Purpose

A clean link is useful as a baseline, but practical VHF/UHF operation involves loss, fading, noise, timing variation, and hardware constraints. The RF lab will make those conditions measurable and repeatable.

## Planned test areas

- Baseline end-to-end messaging and Winlink email transfer.
- Simulated radios and audio-path loopback.
- Packet loss, delay, jitter, noise, fading, and channel-busy scenarios.
- Link startup, disconnect, retries, and recovery behavior.
- Negotiation and throughput measurements for supported modems.
- Compatibility checks across app, gateway, and modem versions.

## Testing principles

- Keep a clean baseline separate from impaired-link test results.
- Record software versions, test inputs, link settings, and evidence.
- Separate simulated test success from real over-the-air validation.
- Do not equate successful RF transport exchange with confirmed application delivery.
- Avoid publishing passwords, private user data, or live station credentials in test artifacts.

## Initial milestones

1. Inventory and migrate reusable FakeRadio and modem test harnesses.
2. Document reproducible clean-link scenarios.
3. Add configurable VHF/UHF link impairment profiles.
4. Capture transfer times, retransmissions, and message delivery outcomes.
5. Share stable test procedures with [relay73-gateway](https://github.com/Relay73/relay73-gateway) and [relay73-modem-adapters](https://github.com/Relay73/relay73-modem-adapters).

## Lab safety

No unattended real RF transmission or station deployment should occur as a side effect of running default tests. On-air testing requires authorization and appropriate station control.

## Related repositories

[Project hub](https://github.com/Relay73/Relay73) | [Gateway](https://github.com/Relay73/relay73-gateway) | [Modem adapters](https://github.com/Relay73/relay73-modem-adapters)

Setup, sample data, and licensing will be documented as the lab tools are migrated.
