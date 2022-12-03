# Changelog

## [1.1.2](https://github.com/miracum/vfps/compare/v1.1.1...v1.1.2) (2022-12-03)


### CI/CD

* fixed chaos workflow and digest-pinned iter8 action ([#42](https://github.com/miracum/vfps/issues/42)) ([89f2f7e](https://github.com/miracum/vfps/commit/89f2f7e7cc457d4ae6db6780eb29664093cecb6e))
* stress test refactor and slsa3 compliance ([#44](https://github.com/miracum/vfps/issues/44)) ([d1ba420](https://github.com/miracum/vfps/commit/d1ba4208ed79bb53567433d2bf070986b08d4216))

## [1.1.1](https://github.com/miracum/vfps/compare/v1.1.0...v1.1.1) (2022-11-29)


### Bug Fixes

* possible null value exception ([ba9fb86](https://github.com/miracum/vfps/commit/ba9fb86edf8fc26503b42553c15f6436b0e229dd))


### Miscellaneous Chores

* added vscode run files ([ba9fb86](https://github.com/miracum/vfps/commit/ba9fb86edf8fc26503b42553c15f6436b0e229dd))
* updated image tag in docker-compose via release-please ([ba9fb86](https://github.com/miracum/vfps/commit/ba9fb86edf8fc26503b42553c15f6436b0e229dd))
* use common config for renovate ([ba9fb86](https://github.com/miracum/vfps/commit/ba9fb86edf8fc26503b42553c15f6436b0e229dd))


### CI/CD

* added +x to argo cli ([73384f1](https://github.com/miracum/vfps/commit/73384f157496c0c72ce2dca1900a203e4058ea43))
* added buildx setup to allow gha caching ([7cd115b](https://github.com/miracum/vfps/commit/7cd115bf26b49fb14aa0efe81751fc30eec7b598))
* added nightly-running chaos test supported by NBomber stress-test ([#37](https://github.com/miracum/vfps/issues/37)) ([a97cffe](https://github.com/miracum/vfps/commit/a97cffe7086c3ab44c4e2df463b383446b52c631))
* fix chaos testing workflow ([faa7cf6](https://github.com/miracum/vfps/commit/faa7cf615a6fdc2c46b82d39b9a1d75f83116f0d))
* log chaos workflow output ([70ea46a](https://github.com/miracum/vfps/commit/70ea46a86024730c4ff33ef7134f8b1436ecad77))
* set read permissions for lint-pr-title job ([318f55b](https://github.com/miracum/vfps/commit/318f55b54b7675668a12a7ae468cc11e27b4fc50))
* set read permissions for lint-pr-title job ([#41](https://github.com/miracum/vfps/issues/41)) ([318f55b](https://github.com/miracum/vfps/commit/318f55b54b7675668a12a7ae468cc11e27b4fc50))
* use miracum-bot token for release please ([#39](https://github.com/miracum/vfps/issues/39)) ([45b21e4](https://github.com/miracum/vfps/commit/45b21e4b133aeafbdb2ddfd7dd4258e9737860d6))

## [1.1.0](https://github.com/miracum/vfps/compare/v1.0.0...v1.1.0) (2022-11-18)


### Features

* expose metrics on a dedicated port to improve security posture ([#35](https://github.com/miracum/vfps/issues/35)) ([514dba8](https://github.com/miracum/vfps/commit/514dba8907412eba54437a38bf157efa6966f5d8))


### Miscellaneous Chores

* **deps:** bumped jaeger tracing version to re-trigger master ci ([c5cd38e](https://github.com/miracum/vfps/commit/c5cd38e10577c07563b48f1e921444989bef3812))
* replace references to former repo ([#33](https://github.com/miracum/vfps/issues/33)) ([af25cbf](https://github.com/miracum/vfps/commit/af25cbf15b84c36c89952a91431f519e807cf2ff))


### CI/CD

* fixed iter8 tests ([#36](https://github.com/miracum/vfps/issues/36)) ([eebbd70](https://github.com/miracum/vfps/commit/eebbd702aa734323796a5592f0581294ef5322da))

## [1.0.0](https://github.com/miracum/vfps/compare/v0.6.0...v1.0.0) (2022-11-09)


### ⚠ BREAKING CHANGES

* updated to stable .NET 7 (#30)

### Features

* updated to stable .NET 7 ([#30](https://github.com/miracum/vfps/issues/30)) ([69a80ae](https://github.com/miracum/vfps/commit/69a80aecec13fd1d389cf48a741827cd8f79809b))


### Miscellaneous Chores

* **deps:** update docker.io/jaegertracing/all-in-one:1.38 docker digest to 14cf294 ([#28](https://github.com/miracum/vfps/issues/28)) ([7552e15](https://github.com/miracum/vfps/commit/7552e15831a27fd753c46f9b2c5459f5e890ce8a))
* **deps:** update docker.io/library/ubuntu:22.04 docker digest to 7cfe754 ([#29](https://github.com/miracum/vfps/issues/29)) ([c45903a](https://github.com/miracum/vfps/commit/c45903a187fdb8e799ea4a5a8ae9e26591d8815c))


### CI/CD

* another attempt at fixing scorecards ([03ce72f](https://github.com/miracum/vfps/commit/03ce72f5ea56ccdb9647a0e6b14093a46cf19c10))
* bumped cosign installer ([c5d942f](https://github.com/miracum/vfps/commit/c5d942f8e1b28629502532da6df7e669442997d4))
* possibly fixed ossf scorecard ([d93b25a](https://github.com/miracum/vfps/commit/d93b25a3daebda5f937ce20c32da9bc943947470))

## [0.6.0](https://github.com/miracum/vfps/compare/v0.5.1...v0.6.0) (2022-10-31)


### Features

* add support for listing pseudonyms in namespace ([#27](https://github.com/miracum/vfps/issues/27)) ([e9cdd82](https://github.com/miracum/vfps/commit/e9cdd8233db5b377de7a04b26701cd6b40b3f178))
* added caching support for pseudonyms ([#24](https://github.com/miracum/vfps/issues/24)) ([0383aec](https://github.com/miracum/vfps/commit/0383aecdcaf6801a3cacc35358a100aafa843b64))
