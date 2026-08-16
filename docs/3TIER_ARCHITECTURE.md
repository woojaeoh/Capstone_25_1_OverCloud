# OverCloud 3-Tier 아키텍처 전환 설계안

- 작성일: 2026-08-11
- 상태: 초안 (설계 확정 전)
- 목적: 재배포(신규 클라우드 이전)를 계기로 현재의 2-Tier(Fat Client) 구조를 3-Tier로 전환한다.

---

## 1. 배경 및 목표

캡스톤은 마감되었고 현재는 리팩토링 단계다. 기존에는 AWS EC2 + RDS(MySQL, 서울 리전) 조합으로 배포했으나 프리티어 무료 기간이 종료되어, AWS에 한정하지 않고 새로운 클라우드로 재배포할 예정이다. 재배포는 인프라를 처음부터 다시 구성하는 작업이므로, 이번이 아키텍처를 개선할 수 있는 가장 저렴한 시점이다.

전환 목표:

1. **DB 자격증명 노출 제거** — 클라이언트 바이너리에 MySQL root 비밀번호가 하드코딩되어 배포되는 문제 해결
2. **OAuth 앱 시크릿 노출 제거** — Google/OneDrive/Dropbox 앱 단위 client secret이 클라이언트 로컬 경로(`C:\key\*.json`)에 의존하는 문제 해결
3. **중앙화된 비즈니스 로직** — 정책(할당량, 티어 선택 등)을 서버에서 일관되게 강제
4. **재배포 유연성** — DB/시크릿 위치를 서버 설정(환경변수 등)으로 분리해 클라우드를 바꿔도 클라이언트 재배포 없이 대응 가능

---

## 2. 현재 아키텍처 진단 (AS-IS) — 2-Tier

폴더는 `DB/`, `OverCloud/Services/`, `UI/overcloud/`로 나뉘어 계층화된 것처럼 보이지만, 실제로는 `overcloud.csproj`가 `DB/overcloud/**/*.cs`와 `OverCloud/Services/**/*.cs`를 전부 `<Compile Include>`로 끌어와 **하나의 WPF 실행 파일**로 컴파일한다.

```
[ WPF 클라이언트 프로세스 (사용자 PC) ]
  ├─ Presentation (Views, XAML)
  ├─ Business Logic (OverCloud/Services — AccountService, QuotaManager, FileUploadManager ...)
  └─ Data Access (DB/overcloud/Repository — MySqlConnection 직접 생성)
         │
         │  MySQL 프로토콜, TCP 3306 직접 접속
         ▼
[ MySQL DB 서버 ]
```

### 확인된 문제

| 문제 | 근거 | 영향 |
|---|---|---|
| DB root 비밀번호 하드코딩 | `UI/overcloud/overcloud/DbConfig.cs:6` — `uid=root;pwd=Wodn134679!!;` | 배포된 모든 클라이언트 exe에 DB 전체 권한 계정이 노출됨 |
| MySQL 포트가 모든 사용자에게 열려있어야 함 | 클라이언트가 DB에 직접 접속 (`AccountRepository.cs` 등 전 Repository) | DB 서버를 인터넷에 노출, 공격 표면 확대 |
| OAuth 앱 시크릿이 로컬 절대경로 의존 | `GoogleAuthHelper.cs:17`, `OneDriveAuthHelper.cs:25`, `DropboxAuthHelper.cs:10` — `C:\key\*.json` | 개발자 PC 외 배포 환경에서는 해당 파일이 없어 OAuth 로그인 자체가 불가능할 가능성이 높음 |
| 비즈니스 로직이 클라이언트에만 존재 | `LoginController.cs` — AccountService/QuotaManager/CloudTierManager 등을 클라이언트가 직접 생성·실행 | 클라이언트를 변조하면 할당량/티어 정책을 우회 가능 |

---

## 3. 목표 아키텍처 (TO-BE) — 3-Tier

```mermaid
flowchart LR
    subgraph T1["Tier 1: Presentation (WPF Client)"]
        UI[Views / ViewModel]
        LocalFS[로컬 파일시스템]
    end

    subgraph T2["Tier 2: Application (신규 API 서버)"]
        Auth[Auth API<br/>로그인/JWT 발급]
        BizAPI[Business API<br/>AccountService, QuotaManager,<br/>CloudTierManager, FileUploadManager 등]
        OAuthProxy[OAuth 토큰 발급 API<br/>refresh_token → 단기 access_token]
        Repo[Repository 계층<br/>DB/overcloud/Repository 이관]
    end

    subgraph T3["Tier 3: Data"]
        MySQL[(MySQL<br/>기존 스키마 그대로)]
        Secrets[(서버 환경변수 / Secret Manager<br/>DB 자격증명, OAuth 앱 시크릿)]
    end

    subgraph EXT["외부: 클라우드 스토리지 제공자"]
        Google[Google Drive API]
        OneDrive[OneDrive / MS Graph API]
        Dropbox[Dropbox API]
    end

    UI -- "HTTPS REST + JWT" --> Auth
    UI -- "HTTPS REST + JWT" --> BizAPI
    UI -- "access_token 요청" --> OAuthProxy
    BizAPI --> Repo
    Repo -- "MySQL 프로토콜" --> MySQL
    Auth --> Repo
    OAuthProxy -- "앱 시크릿 사용" --> Secrets
    OAuthProxy -- "refresh 요청" --> Google
    OAuthProxy -- "refresh 요청" --> OneDrive
    OAuthProxy -- "refresh 요청" --> Dropbox

    UI -- "파일 바이트 업/다운로드<br/>(access_token 사용, 서버 경유 안 함)" --> Google
    UI --> OneDrive
    UI --> Dropbox
```권

핵심 원칙: **DB 접속과 비밀 정보 관리는 서버로 옮기되, 대용량 파일 바이트 전송은 지금처럼 클라이언트 ↔ 클라우드 제공자 직접 경로를 유지한다.** (근거는 5.1 참고)

---

## 4. 계층별 책임 정의

### 4.1 Presentation Tier — WPF 클라이언트 (기존 UI 프로젝트)

- 유지: Views/XAML, 로컬 파일시스템 접근, 클라우드 제공자와의 실제 업/다운로드 바이트 전송
- 제거: `DbConfig.cs`의 connectionString, `MySqlConnection` 직접 생성 코드, `C:\key\*.json` 직접 로드 코드
- 추가: API 서버 호출용 `HttpClient` 래퍼, JWT/세션 토큰 보관, 로그인 시 API 서버로부터 access_token 발급받는 흐름

### 4.2 Application Tier — 신규 API 서버 (ASP.NET Core Web API 장)

기존 `OverCloud/Services/*`, `DB/overcloud/Repository/*`를 그대로 이 프로젝트로 이관한다 (코드가 이미 UI와 분리돼 있어 이식 자체는 크지 않음). `LoginController.cs`가 이미 사실상의 조립부(composition root) 역할을 하고 있으므로, 이를 ASP.NET Core의 DI 컨테이너 등록 코드로 변환하는 것이 자연스러운 시작점이다.

- 인증/인가: 로그인 API, JWT 발급/검증
- 비즈니스 로직: `AccountService`, `QuotaManager`, `CloudTierManager`, `FileUploadManager`/`DownloadManager`/`DeleteManager`/`CopyManager`, `CooperationManager` 등 — 정책 판단은 전부 서버에서 수행
- OAuth 토큰 중개: 사용자별 `refresh_token`은 DB에 보관(기존과 동일), 서버가 앱 시크릿으로 `access_token`을 교환해 클라이언트에는 **만료시간이 짧은 access_token만** 내려줌 (앱 시크릿 자체는 클라이언트에 절대 전달하지 않음)
- DB 자격증명 보관: connectionString을 서버 환경변수/Secret Manager로 관리

### 4.3 Data Tier — MySQL

- 스키마 변경 없음. 접속 주체만 "클라이언트 전체"에서 "API 서버 하나"로 축소됨 → DB 서버를 사설 네트워크로 격리하고 API 서버에서만 접근 허용 가능해짐 (보안 개선)
- 기존 DB 백업/데이터는 그대로 신규 인프라의 MySQL로 마이그레이션(mysqldump 등)하면 됨

---

## 5. 핵심 설계 결정사항

### 5.1 파일 전송 경로: 서버 프록시 vs 클라이언트 직접 전송

**결정: 클라이언트 직접 전송 유지 (서버 프록시 안 함)**

- 현재 `FileUploadManager.cs`는 클라이언트가 `service.UploadFileAsync(...)`로 Google/OneDrive/Dropbox에 직접 바이트를 전송한다.
- 만약 API 서버가 파일 바이트까지 중계하면, 같은 데이터가 "클라이언트→API 서버→클라우드 제공자"로 두 번 왕복하게 되어 서버 대역폭 비용과 지연이 두 배로 늘어난다. 무료/저비용 클라우드로 재배포하는 상황에서는 이 비용이 특히 부담된다.
- 따라서 서버는 **메타데이터(어느 계정에 얼마나 저장할지, 할당량 등)와 access_token 발급**만 담당하고, 실제 바이트는 지금처럼 클라이언트가 클라우드 제공자와 직접 주고받는다.

**번복 이력**: `/api/files` 최초 구현(2026-08-15)에서 이 결정을 어기고 `POST /api/files/upload`/`GET /api/files/{fileId}/download`가 바이트를 직접 받고 돌려주는 서버 프록시 구조로 만들어졌다. 의도적인 재검토가 아니라, `FileUploadManager`/`FileDownloadManager`를 "이미 컴파일되는 코드니 그대로 재사용"하는 데만 집중하다 이 문서의 5.1 결정을 확인하지 않은 **실수**였다. 이후 같은 날 바로 원안대로 되돌림: 서버는 `POST /api/files/select-storage`(업로드 대상 스토리지 결정), `POST /api/files/confirm-upload`(업로드 완료 후 메타데이터 기록 + 할당량 갱신), `GET /api/files/{fileId}/location`(다운로드 전 어느 클라우드의 무슨 파일인지 조회), `DELETE /api/files/{fileId}`(클라이언트가 클라우드에서 이미 지운 뒤 메타데이터만 정리)로 메타데이터만 다루고, 클라이언트가 `POST /api/oauth/{provider}/access-token`으로 받은 토큰으로 클라우드 API를 직접 호출해 바이트를 주고받는다. `GoogleDriveService`/`OneDriveService`/`FileUploadManager`/`FileDownloadManager`/`FileDeleteManager`는 서버 DI에서 전부 제거했다 — 서버는 더 이상 클라우드 제공자에 직접 접속하지 않는다.

### 5.2 인증/인가 흐름

1. 클라이언트가 API 서버에 아이디/비밀번호로 로그인 요청
2. 서버가 자격증명 검증 후 JWT(또는 세션 토큰) 발급
3. 이후 모든 API 호출에 JWT를 첨부
4. 클라우드 스토리지 작업이 필요할 때, 클라이언트가 API 서버에 "이 계정의 access_token 줘" 요청 → 서버가 DB의 refresh_token + 서버 보관 앱 시크릿으로 access_token 교환 → 클라이언트에 access_token만 반환
5. 클라이언트는 이 access_token으로 클라우드 제공자 API를 직접 호출

### 5.3 OAuth 앱 시크릿 이관 계획

| 현재 | 이관 후 |
|---|---|
| `GoogleAuthHelper.cs` → `C:\key\credential.json` | 서버 환경변수 또는 Secret Manager |
| `OneDriveAuthHelper.cs` → `C:\key\onedrive_credential.json` | 서버 환경변수 또는 Secret Manager |
| `DropboxAuthHelper.cs` → `C:\key\dropbox.json` | 서버 환경변수 또는 Secret Manager |

최초 OAuth 인가(사용자 동의 화면 띄우기)는 브라우저 리다이렉트가 필요해 클라이언트가 트리거하지만, **콜백에서 받은 authorization code를 access/refresh token으로 교환하는 단계는 서버가 대행**한다 (client secret이 필요한 단계이므로).

### 5.4 DB 자격증명 이관 계획

`DbConfig.cs`의 connectionString(현재 root 계정)을 제거하고:
- API 서버 전용 DB 계정을 새로 발급 (root 아님, 필요한 테이블에 대한 권한만)
- connectionString은 서버 환경변수(`.env` 또는 클라우드 provider의 secret 저장소)로 관리

### 5.5 JWT 정책 (access/refresh 분리)

JWT는 무상태(stateless)라 발급 후에는 서버가 개입해 즉시 무효화하기 어렵다. 계정 정지·할당량 정책 변경이 즉시 반영돼야 하므로 다음 원칙으로 간다:

- **access token**: 신원 증명 용도로만 사용, TTL 15~30분. 짧게 잡아 탈취/오남용 시 피해 시간을 제한한다.
- **authorization(정지 여부, 할당량 등)은 토큰 내용이 아니라 매 요청마다 DB 최신 상태로 판단**한다 — 즉 토큰이 유효해도 서버가 "이 계정 지금 정지 상태인가?"는 항상 다시 확인. 이렇게 하면 별도의 revocation list 없이도 정지가 즉시 반영된다.
- **refresh token**: DB에 저장(사용자당 1개, 로그인 시 갱신). 계정 정지/비밀번호 변경 시 DB의 refresh token을 무효화하면 재로그인이 막히고, 이미 발급된 access token은 TTL 만료 후 자연 소멸한다.

### 5.6 LAN 전송 signaling — DB 직접 조회 문제

`LanTransferService.SendFileAsync`가 상대방 IP를 얻기 위해 `_accountRepository.GetLocalIp(targetUserId)`를 **클라이언트에서 직접** 호출하고 있고 (`OverCloud/Services/LanTransferService.cs:145`), 이는 `Account` 테이블을 `SELECT local_ip FROM Account WHERE ID=@id AND is_online=1`로 직접 쿼리하는 것이다(`DB/overcloud/Repository/AccountRepository.cs:178`). 로그인/로그아웃 시 `local_ip`/`is_online`을 갱신하는 것도 클라이언트가 직접 UPDATE한다(`AccountRepository.cs:163`).

3-Tier 전환 후 클라이언트가 DB에 못 붙으면 이 조회·갱신이 그대로 실패해 **LAN 전송 기능 자체가 깨진다.** P2P 파일 전송(바이트 전송)은 여전히 클라이언트 간 직접 TCP로 남겨두되, signaling(피어 IP 조회, 온라인 상태 갱신)만 API로 옮긴다:

- `POST /api/presence` — 로그인/로그아웃 시 `local_ip`, `is_online` 갱신
- `GET /api/presence/{targetUserId}` — 상대방이 온라인이면 IP 반환, 아니면 null

이 두 엔드포인트가 없으면 LAN 전송은 3-Tier 전환과 동시에 회귀(regression)한다.

### 5.7 동시성 (Phase 2 선행 조건)

3-Tier 전환으로 여러 사용자 요청이 API 서버 한 프로세스에서 진짜 동시(멀티스레드)로 처리되기 시작하면서 2-Tier(클라이언트 프로세스당 사용자 1명)에서는 드러나지 않던 위험들. QuotaManager 등을 API 엔드포인트에 연결하는 Phase 2 전에 처리해야 한다.

- [ ] **`StorageSessionManager` 전역 static 캐시 제거, 매 요청 DB 재계산으로 전환** — `OverCloud/Services/StorageManager/StorageSessionManager.cs:26`의 `static List<CloudQuotaInfo> Quotas`는 프로세스 전역이며 사용자별 파티션이 없다. 이미 `Api/OverCloud.Api.csproj:27`의 `Compile Include`로 API 프로젝트에 컴파일되어 들어가 있어서, QuotaManager를 엔드포인트에 연결하는 순간 `GetTotalAvailableCapacityKB()`(79행)처럼 리스트 전체를 합산하는 메서드가 서로 다른 사용자의 용량 정보를 섞어버린다. DI lifetime(Scoped/Singleton)으로는 해결 불가 — `static class`라 무조건 프로세스 전역이다. QuotaManager를 API에 연결하기 전 필수 선행 작업.
- [ ] **`AccountFile_Redistribution` 대상 storage row에 `SELECT ... FOR UPDATE`** — `QuotaManager.cs:159` 재분배 로직은 후보 storage를 조회(`GetCandidateStorages`)한 뒤 업로드가 끝날 때까지 아무 락도 걸지 않아, 재분배 두 건(또는 재분배+일반 업로드)이 같은 target storage를 동시에 후보로 골라 쿼터를 초과시킬 수 있다. 대상 `CloudStorageInfo` row를 트랜잭션 안에서 `SELECT ... FOR UPDATE`로 잠그면 **그 storage의 쿼터 초과만** 방지한다 — 완전한 레이스 해결은 아니며, 파일 단위 부분 실패(일부 파일만 이동)나 청크 업로드 중단 시 롤백 미구현(`// TODO: uploadedChunks 순회하며 삭제 구현 가능`) 같은 나머지 문제는 그대로 남는 것으로 알려진 한계로 명시한다.
- [ ] **Repository 계층 동기/비동기 확인, 가능하면 async로 전환** — 확인 결과 `AccountRepository`/`FileRepository`/`StorageRepository` 전부 async 메서드가 0개이며, 모든 메서드가 `MySqlConnection.Open()` / `ExecuteReader()` / `ExecuteNonQuery()` / `ExecuteScalar()` 동기 호출로 구현돼 있다. ASP.NET Core에서 동기 DB 호출은 요청당 스레드풀 스레드 하나를 DB 왕복이 끝날 때까지 통째로 점유한다 — 동시 요청이 늘어나면(여러 팀원이 동시에 업로드/다운로드) 스레드풀 고갈로 이어져 관련 없는 다른 요청까지 타임아웃될 수 있다(8절 위험과 연결). `OpenAsync`/`ExecuteReaderAsync`/`ExecuteNonQueryAsync`/`ExecuteScalarAsync`로 전환이 필요하지만, Repository 3개 클래스 + 인터페이스 + 이를 호출하는 모든 Service 메서드까지 연쇄적으로 바뀌는 큰 기계적 변경이라 전체를 한 번에 진행하기보단 메서드 하나로 패턴을 먼저 잡아 검토받은 뒤 확산하는 걸 권장.

---

## 6. API 서버 엔드포인트 초안

| 영역 | 메서드/경로 (예시) | 대응하는 기존 코드 |
|---|---|---|
| 인증 | `POST /api/auth/login` | `LoginController`, `AccountRepository` |
| 계정 | `GET/POST /api/accounts` | `AccountService`, `AccountRepository` |
| 스토리지 연결 | `POST /api/storages/{provider}/authorize-callback` | `GoogleAuthHelper`, `OneDriveAuthHelper`, `DropboxAuthHelper` |
| 토큰 발급 | `GET /api/storages/{id}/access-token` | `GoogleTokenProvider`, `OneDriveTokenRefresher`, `DropboxTokenRefresher` |
| 파일 메타데이터 | `GET/POST/DELETE /api/files` | `FileRepository`, `FileUploadManager` 등 (메타데이터만) |
| 할당량 | `GET /api/quota` | `QuotaManager` |
| 협업 | `GET/POST /api/coop/*` | `CooperationManager`, `CoopUserRepository` |
| 이슈 | `GET/POST /api/issues/*` | `FileIssueRepository`, `FileIssueCommentRepository` |
| LAN signaling | `POST /api/presence`, `GET /api/presence/{targetUserId}` | `LanTransferService.SendFileAsync`, `AccountRepository.GetLocalIp` (5.6 참고) |

> LAN 전송의 **P2P 바이트 전송 자체**(`LanTransferService`의 TCP 송수신, `RelayOverCloud`)는 이번 3-Tier 전환 범위와 무관하게 클라이언트에 그대로 둔다. 다만 피어 IP/온라인 상태 조회는 DB 접근이므로 위 `presence` 엔드포인트로 옮겨야 한다 (5.6 참고).

---

## 7. 마이그레이션 단계

- [ ] **Phase 1 — API 서버 뼈대**: ASP.NET Core Web API 프로젝트 생성, `DB/overcloud/Repository`·`OverCloud/Services` 이관, `LoginController`를 DI 등록으로 전환
  - [x] 프로젝트 생성 + `OverCloud.Api.csproj`에서 `DB/overcloud`, `OverCloud/Services` 전체를 Link로 참조 — 파일을 옮기지 않고 소스를 공유해 `AccountService`/`QuotaManager`/`FileUploadManager` 등이 이미 API 프로젝트에서도 컴파일됨
  - [ ] `LoginController`가 수동으로 조립하는 나머지 서비스(`FileUploadManager`, `CooperationManager` 등)를 DI 등록으로 전환 — 해당 서비스를 쓰는 엔드포인트를 추가할 때마다 점진적으로 진행
- [x] **Phase 2 — 인증 계층**: 로그인 API + JWT 발급/검증 미들웨어 추가
  - [x] `/api/auth/login`, `/api/auth/refresh`(refresh token rotation 포함), `/api/auth/logout`
  - [x] 보호된 엔드포인트에 `[Authorize]` + IDOR 체크(sub == route userId)
  - [ ] 정지 계정 매 요청 DB 재확인(5.5) — **의도적으로 미룸**: 계정을 정지시킬 관리자 수단(API/DB) 자체가 아직 없어서 지금 만들어도 검증 불가. Phase 3/4에서 관리 기능이 생길 때 함께 설계 예정
- [ ] **Phase 3 — OAuth 시크릿 이관**: 3개 Auth Helper를 서버 API로 이동, access-token 발급 엔드포인트 구현
  - [x] Google: `POST /api/oauth/google/access-token` — client_secret은 서버 설정(`OAuth:Google:ClientSecret`)에서만 읽음, `GetCloud(cloudStorageNum, userId)`로 IDOR 방지, refresh token 만료/폐기(`invalid_grant`)는 401로 구분 응답 (테스트 완료: 정상 200, 만료 401 모두 확인)
  - [x] OneDrive: `POST /api/oauth/onedrive/access-token` — Google과 동일 패턴, `client_secret` 없이 `client_id`만 사용(public client), 공통 `OAuthRefreshTokenInvalidException`으로 401 구분 응답 (테스트 완료: 정상 200 확인). 계정 추가 시 캐시된 브라우저 세션이 자동 재사용되던 문제를 `prompt=select_account` 추가로 별도 수정
  - [ ] Dropbox: **의도적으로 보류** — 사용자별 OAuth 연동 흐름 자체가 없고(고정 공유 credential 파일에서 refresh_token을 읽는 구조), 이 구조적 한계를 그대로 둔 채 엔드포인트만 추가하는 게 의미가 없다고 판단해 별도 결정 전까지 미룸
  - [ ] 최초 계정 연동(authorization code → refresh_token 교환) 서버 이관 — Phase 4로 이월 (이번 라운드는 access-token 재발급만 범위)
- [ ] **Phase 4 — 클라이언트 리팩토링**: `DbConfig.cs`/직접 DB 접속 코드 제거, `LanTransferService`의 DB 직접 조회를 `presence` API 호출로 교체, API 클라이언트로 교체, 로그인/토큰 발급 흐름 변경
  - [x] `OverCloud/Services/OverCloudApiClient.cs` 추가 — 클라이언트가 API 서버와 처음 통신을 시작하는 지점. 로그인 시 기존 DB 직접 인증과 병행으로 `POST /api/auth/login` 호출해 JWT 발급·보관 (API 서버 다운 시에도 기존 로그인은 그대로 동작하도록 예외를 내부에서 삼킴, 테스트 완료)
  - [x] presence API(5.6) 연동 — `LoginWindow`(온라인 전환), `LanTransferService.SendFileAsync`(피어 IP 조회), `App.xaml.cs`(종료 시 오프라인 전환)에서 API 우선 시도 후 실패 시 기존 DB 직접 접속으로 폴백. DB 리포지토리 코드 자체는 그대로 남겨둬 언제든 되돌릴 수 있음 (테스트 완료)
  - [x] `GET /api/quota/{userId}` 추가 — DB에 저장된 값을 합산만 함(클라우드 제공자 실시간 조회는 안 함), SYSTEM 더미 스토리지(-1) 제외
  - [x] `/api/files` — 5.1(서버는 바이트를 중계하지 않는다) 원칙에 맞춰 메타데이터/할당량만 다룬다:
    - `GET /api/files/{parentFolderId}` — 목록 조회(최상위 -1)
    - `POST /api/files/select-storage` — 파일 크기(KB)를 넣으면 `CloudTierManager`의 기존 티어 로직으로 업로드 대상 스토리지 결정
    - `POST /api/files/confirm-upload` — 클라이언트가 `select-storage`로 정해진 스토리지에 `/api/oauth/{provider}/access-token`으로 받은 토큰으로 클라우드 API를 **직접** 호출해 업로드를 마친 뒤, 그 결과(cloudFileId)를 알려주면 서버가 `CloudFileInfo` 행 생성 + 할당량 갱신만 함
    - `GET /api/files/{fileId}/location` — 다운로드 전 소유자 확인 후 어느 클라우드의 무슨 파일인지(cloudStorageNum, cloudFileId)만 알려줌, 실제 바이트는 클라이언트가 클라우드에서 직접 받음
    - `DELETE /api/files/{fileId}` — 클라이언트가 클라우드에서 이미 지운 뒤 메타데이터 행만 정리(업로드와 대칭 계약)
    - 서버 DI에서 `GoogleDriveService`/`OneDriveService`/`FileUploadManager`/`FileDownloadManager`/`FileDeleteManager`를 전부 제거함 — 서버는 클라우드 제공자에 직접 접속하지 않는다. `QuotaManager`는 할당량 산술(`UpdateQuotaAfterUploadOrDelete`, DB 조회만 함)에만 쓰고 그 외 메서드는 호출하지 않음.
    - fileId 소유자(`CloudFileInfo.ID`)가 sub와 다르면 403. 단일 파일만 우선 구현 — 분산 저장(`Upload_Distributed`/`DownloadAndMergeFile`/`Delete_DistributedFile`)은 아직 미이관.
    - Dropbox는 여전히 제외 (위 Phase 3 결정과 동일 이유).
    - **주의(미해결, 별도 확인 필요)**: 이번에 `FileDeleteManager.Delete_File`/`Delete_DistributedFile`과 `QuotaManager.AccountFile_Redistribution`(업로드 쪽) 코드를 다시 보다가 발견한 것 — 이미 DB에 저장된 `CloudFileInfo.FileSize`(이미 KB 단위)를 재사용해 `UpdateQuotaAfterUploadOrDelete`를 호출하는 자리에서 `file.FileSize / 1024`처럼 또 나누고 있어, 최초 업로드 때 계산한 델타(바이트→KB, 1회 나눔)와 단위가 안 맞는 것으로 보임. 삭제/재분배를 반복하면 추적된 사용량이 실제보다 점점 커지는 방향으로 드리프트할 수 있음 — 이번 `/api/files` 작업 범위 밖이라 건드리지 않았고, 새로 만든 `DELETE /api/files/{fileId}`는 `file.FileSize`를 그대로(추가로 나누지 않고) 넘기도록 짰음. 원본 클라이언트 코드 쪽은 별도로 확인 필요.
  - [x] 클라이언트를 위 `/api/files` + `access-token` 조합으로 실제로 연결(5.1의 "클라이언트가 직접" 쪽 절반):
    - `OverCloudApiClient`에 `GetOAuthAccessTokenAsync`/`SelectStorageAsync`/`ConfirmUploadAsync`/`GetFileLocationAsync`/`GetRemainingQuotaBytesAsync` 추가
    - `UI/overcloud/overcloud/CloudApi/GoogleDriveTokenClient.cs`, `OneDriveTokenClient.cs` 신규 — 토큰만 받아 클라우드 API를 직접 호출하는 클라이언트 전용 클래스(API 서버 프로젝트로는 링크되지 않음). 기존 `OverCloud.Services.FileManager.DriveManager.GoogleDriveService`/`OneDriveService`를 그대로 재사용하지 않은 이유: 그 클래스들은 `storageRepo.GetCloud`(DB 직접 조회)와 `GoogleTokenProvider`/`OneDriveTokenRefresher`(로컬에서 `client_secret`으로 refresh_token 교환)에 묶여 있어, 재사용하면 겉보기엔 API로 옮긴 것 같아도 실제로는 DB 직접 접속과 시크릿 노출이 그대로 남는다
    - `UploadManager.ProcessUpload`/`DownloadManager.ProcessDownload`가 `CloudTierManager.SelectBestStorage`(DB 직접) 대신 `select-storage`→`access-token`→토큰 클라이언트→`confirm-upload`/`location` 흐름을 탐 — **교체**이지 병행이 아니라서 API 서버가 꺼져 있으면 업/다운로드가 실패한다(의도된 동작, presence처럼 DB 폴백을 두지 않음). `CloudTierManager`는 `UploadManager`/`TransferManager`/`LoginWindow.xaml.cs` 생성자에서 완전히 제거됨. 분산 저장(`Upload_Distributed`/`DownloadAndMergeFile`)은 여전히 기존 `FileUploadManager`/`FileDownloadManager`(DB 직접) 경로로 남아있음 — 단일 파일만 이관됨
    - `HomeView.xaml.cs`/`SharedAccountView.xaml.cs`의 업로드 전 용량 체크(`CloudTierManager.GetTotalRemainingQuotaInBytes`, DB 직접)도 `GET /api/quota/{userId}`로 교체
    - **`GET /api/quota/{userId}` 인가 범위 확장 + 신규 보안 체크**: `SharedAccountView`가 조회하는 `_currentAccountId`는 로그인한 본인(sub)이 아니라 협업 계정 ID라서, 기존처럼 `sub == userId`만 허용하면 항상 403이 난다. 그래서 `sub == userId`이거나 `ICoopUserRepository.connected_cooperation_account_nums(sub)`에 `userId`가 포함된 경우(= sub가 그 협업 계정의 정당한 멤버)까지 허용하도록 넓힘. **주의**: 이건 단순 이관이 아니라 새로 추가된 검증이다 — 기존 클라이언트 DB 직접 경로(`CloudTierManager.GetTotalRemainingQuotaInBytes`)는 이 멤버십을 전혀 확인하지 않고 클라이언트가 넘긴 accountId를 그대로 신뢰하고 있었다. `ICoopUserRepository`/`CoopUserRepository`는 이미 Phase 1 소스 링크로 API 프로젝트에 들어와 있어 새 DI 등록만 추가함(새 파일 없음).
    - **버그 수정: 협업 계정 업로드 시 파일이 본인 계정에 저장돼 목록에 안 보이던 문제** — `select-storage`/`confirm-upload`/`오auth access-token` 세 엔드포인트가 전부 대상 계정을 파라미터로 안 받고 항상 `sub`(로그인한 본인) 기준으로만 동작했다. `SharedAccountView`에서 업로드하면 `UploadManager.ProcessUpload`가 `userId=_currentAccountId`(협업 계정)를 들고 있어도 서버는 그걸 모르고 `sub`의 개인 스토리지를 골라 `CloudFileInfo.ID = sub`로 저장 — 실제 파일은 로그인한 사람의 개인 클라우드에 올라가고, DB 행도 그 사람 소유로 찍혀서 협업 계정 목록(`WHERE ID = 협업계정`)엔 안 뜨는 상태가 됐다. `SelectStorageRequest`/`ConfirmUploadRequest`/`OAuthAccessTokenRequest`에 `UserId` 필드를 추가하고, 세 엔드포인트 전부 `IsAuthorizedForAccount(sub, req.UserId, coopUserRepository)`(본인 또는 협업 멤버)로 검증하도록 고침. `GET /api/files/{fileId}/location`·`DELETE /api/files/{fileId}`도 소유자 판단을 `sub` 대신 `file.ID`(실제 소유자) 기준 `IsAuthorizedForAccount`로 바꿔 같은 문제를 예방(다운로드/삭제는 아직 실사용 전이라 증상은 안 났었지만 동일한 결함). `GET /api/files/{userId}/{parentFolderId}`(아직 클라이언트가 안 씀)도 같이 정리. 공용 로컬 함수 `IsAuthorizedForAccount`를 `Program.cs`에 추가해 `/api/quota`와 로직을 공유.
    - **버그 수정: `select-storage` 실패 시 "용량 부족(분산 저장 대상)"과 "서버 다운(폴백 금지 대상)"이 구분 안 되던 문제** — `OverCloudApiClient.SelectStorageAsync`가 409(진짜 용량 부족)와 네트워크 예외/기타 실패(서버 다운)를 전부 `null`로 반환했고, `UploadManager.ProcessUpload`는 `null`이면 무조건 분산 저장(`Upload_Distributed`, DB 직접 접속 구버전 경로)으로 폴백했다. 그 결과 API 서버가 완전히 죽어도 MySQL만 살아있으면 조용히 구버전 경로로 새서 업로드가 "성공"해버릴 수 있었다 — 이 세션에서 도입한 "API 다운 시 폴백 없이 실패 처리" 원칙(5.1)과 어긋남. `SelectStorageAsync`가 `SelectStorageResult(SelectedStorage Storage, bool ServerUnreachable)`를 반환하도록 바꿔, 409만 `ServerUnreachable: false`(분산 저장 폴백 대상)로, 그 외(네트워크 예외/401/500 등)는 `ServerUnreachable: true`로 구분. `UploadManager.ProcessUpload`는 `ServerUnreachable`이면 분산 저장을 시도하지 않고 즉시 실패 처리하며 `item.Status`에 "서버에 연결할 수 없습니다"를 표시.
    - **정책 변경: `CloudTierManager.SelectBestStorage`가 최우선순위 클라우드 하나만 확인하도록 변경** — 기존엔 1티어(OneDrive)가 파일 전체를 못 담으면 2티어(GoogleDrive), 3티어(Dropbox) 순으로 "혼자서 전체를 담을 수 있는 클라우드"를 계속 찾아서, 1티어 잔여 용량이 파일보다 조금이라도 작으면 파일 전체가 통째로 다음 티어에 저장됐다(1티어 여유 공간은 낭비). 5번(분산 저장) 테스트 중 "1티어 잔여 200MB인데 256MB 파일이 분산 없이 2티어(GoogleDrive)로 통째로 들어간다"는 관찰로 발견 — 여러 저용량 클라우드를 하나처럼 묶어 쓰는 오버클라우드의 취지에 맞지 않는다고 판단해, 최우선 클라우드 하나만 확인하고 그게 부족하면 바로 `null`을 반환해 분산 저장(`GetStoragePlan`)으로 위임하도록 바꿈. `GetStoragePlan`은 이미 티어 순으로 "가능한 만큼 채우고 모자라면 다음 티어로 이어서" 채우는 로직이라 정책과 맞음. 영향 범위: API 서버 `select-storage`(그대로 409로 넘어가 분산 폴백), 클라이언트 `FileCopyManager`(파일 복사 기능은 분산 폴백이 없어 이제 "복사할 저장공간이 부족합니다"로 실패하는 케이스가 늘어남 — 복사의 분산 구현은 범위 밖), 미사용 레거시 `FileUploadManager.file_upload`.
  - [x] `/api/issues/*` 추가 — `FileIssueInfo`/`FileIssueComment`/`FileIssueMapping` 세 테이블 CRUD를 API로 이관. 기존 클라이언트 코드(`IssueManageView`/`IssueDetailView`/`IssueInfoEditView`/`SharedAccountView.Button_CreateIssue_Click`)는 DB 직접 접속으로 아무 소유권 확인 없이 issue_id/comment_id를 그대로 받아 조작했다 — 오늘 반복한 패턴(소유자 확인 + IDOR 체크) 그대로 적용:
    - `POST /api/issues`(생성, 선택한 파일들과 매핑까지 한 번에), `GET /api/issues/{coopId}`(전체 목록), `GET /api/issues/by-file/{fileId}`(파일별 이슈), `PUT /api/issues/{issueId}`(수정), `DELETE /api/issues/{issueId}`(삭제, 매핑도 같이 정리), `GET /api/issues/{issueId}/files`, `GET/POST /api/issues/{issueId}/comments`
    - `IFileIssueRepository`에 `GetIssueById(int issueId)` 신규 추가 — 기존엔 없어서 issue_id 하나로 "이 이슈가 어느 협업 계정 소유인지" 확인할 방법이 없었다(인가에 필수).
    - 인가: 이슈 단위 조작은 `GetIssueById(issueId).ID`(소유 협업 계정)를 `IsAuthorizedForAccount(sub, ...)`로 확인. 파일별 이슈 조회는 그 파일의 `file.ID`로 확인.
    - `CreatedBy`/`CommenterId`는 클라이언트가 보낸 값이 아니라 토큰의 `sub`를 그대로 씀(위조 방지) — 기존 클라이언트 코드는 `_user_id`를 그대로 실어 보내고 있었어서 이론상 스푸핑 가능했다.
    - `POST /api/issues`에서 초기 파일 매핑 시 각 `fileId`가 실제로 `req.CoopId` 소유인지 확인 후에만 매핑(기존 클라이언트 코드엔 이 확인이 없었음 — 신규 검증).
    - `UpdateIssueStatus`/`AssignIssue`(단독), `DeleteComment`/`UpdateComment`/`GetCommentById`, `AddMapping`/`DeleteMapping`(단독)은 실제 호출하는 클라이언트 코드가 없어 이번엔 API로 안 옮김 — 필요해지면 같은 패턴으로 추가.
    - **버그 수정: 잘못된 `assignedTo`가 원시 MySQL FK 예외를 그대로 500으로 노출** — Swagger 검증 중 발견. `assigned_to`는 `account.ID`를 참조하는 FK라, 존재하지 않거나 그 협업 계정 멤버가 아닌 값을 넘기면 `MySqlException`(FK 제약 위반)이 그대로 500 응답에 노출됐다(테이블/제약조건 이름까지 드러남). `POST /api/issues`·`PUT /api/issues/{issueId}` 모두 저장 전에 `coopUserRepository.GetUsersByCoopId(coopId)`로 실제 멤버인지 확인해 아니면 400을 반환하도록 고침. `GetUsersByCoopId`가 `ICoopUserRepository` 인터페이스엔 없고 구현체(`CoopUserRepository`)에만 있어서 인터페이스에 추가함.
  - [x] `/api/coop/*` 추가 — `CoopUserRepository`(생성/가입/탈퇴/멤버 조회)를 API로 이관. 이슈 클라이언트 교체 전에 먼저 필요(코업 목록/멤버 목록이 이슈 화면 곳곳에서 쓰임):
    - `POST /api/coop`(생성), `POST /api/coop/join`(가입), `POST /api/coop/{coopId}/leave`(탈퇴), `GET /api/coop/mine`(본인이 속한 협업 계정 목록), `GET /api/coop/{coopId}/members`(멤버 목록, `IsAuthorizedForAccount`로 인가)
    - **비밀번호 처리는 사용자 확인 후 평문 유지로 결정** — `Add_cooperation_Cloud_Storage_pro_to_DB`/`Join_cooperation_Cloud_Storage_pro_to_DB`가 원래부터 salt+해시(일반 계정, `RegisterWindow`→`PasswordHasher`) 없이 평문으로 저장/비교하고 있었다. DB 직접 접속일 때보다 HTTP API로 오가면 노출 경로가 하나 늘어난다는 점을 알리고 하드닝 여부를 물었으나, 동작 변경 없이 그대로 이관하기로 결정 — 하드닝은 필요해지면 별도로 논의.
    - **버그 예방**: `Join_cooperation_Cloud_Storage_pro_to_DB`는 이미 멤버인 상태로 다시 호출하면(중복 INSERT) 원시 예외/중복 행 위험이 있어, 호출 전에 `connected_cooperation_account_nums(sub)`로 이미 멤버인지 확인해 409를 반환하도록 가드 추가(기존 클라이언트 코드엔 이 확인이 없었음).
    - 얇은 pass-through인 `CooperationManager`(Services 계층)는 거치지 않고 `ICoopUserRepository`를 엔드포인트에서 직접 호출 — `/api/issues/*`와 동일한 패턴.
  - [ ] 스토리지 추가/삭제 순차 추가
    - 스토리지 추가/삭제(`AccountService.Add_Cloud_Storage`/`Delete_Cloud_Storage`)는 `/api/files`보다 뒤로 미룸: 추가는 `GoogleAuthHelper.AuthorizeAsync` 등 로컬 브라우저 팝업이 필요한 인터랙티브 OAuth라 서버에서 그대로 못 돌림(클라이언트에 남기고 결과만 서버로 보내는 방식으로 재설계 필요), 삭제는 `AccountFile_Redistribution`으로 실제 파일을 다른 클라우드에 재업로드하므로 이번에 만든 파일 업/다운로드 위에서만 안전하게 구현 가능 — 재분배 자체도 5.1에 맞춰 클라이언트가 직접 업로드하는 구조로 다시 설계해야 함.
  - [ ] **DbConfig.cs/직접 DB 접속 코드 완전 제거** — 위 항목이 전부(특히 파일/협업/이슈) 끝나기 전까지는 시도하지 않음. 지금 시도하면 API로 아직 이관 안 된 기능(업로드/다운로드/할당량 갱신/협업/이슈)이 전부 깨짐
  - ⚠️ DB 직접 접속 코드를 실제로 지우는 순간부터는 신규 API 서버 없이 클라이언트가 아예 동작하지 않는다 (빅뱅 전환). 위 항목이 전부 끝나고 나서, 아래 롤백 계획을 먼저 준비한 뒤 진행할 것.
- [ ] **Phase 5 — 인프라 결정 및 배포**: 신규 클라우드 선정(무료/저비용 MySQL 호스팅 포함), API 서버 배포, DB 데이터 마이그레이션
  - **롤백 계획**: 구버전 클라이언트 설치파일을 별도 보관, 신규 스택을 일정 기간 스모크 테스트한 뒤에만 배포 링크(`Server/public/download.html`)를 전환, 구 DB(RDS) 스냅샷은 신규 스택 안정화 확인 후 최소 N일(예: 2주) 보존 후 폐기
- [ ] **Phase 6 — 보안 마감**: DB 계정을 root→최소권한 계정으로 교체, HTTPS 적용, 기존 노출됐던 비밀번호/시크릿 전부 로테이션(재발급), 로그인/토큰 발급 엔드포인트에 rate limiting 적용, 전 엔드포인트에 입력 검증 및 IDOR(내 리소스만 접근 가능한지) 점검

---

## 8. 리스크 및 미해결 이슈

- **해결됨 — `CloudFileInfo` 루트 sentinel 행 부트스트랩**: `FileRepository.GetFullPath`는 `parent_folder_id` 체인을 `-2`를 만날 때까지 타고 올라가는데(최상위 파일들의 `parent_folder_id`는 관례상 `-1`), `file_id=-1, parent_folder_id=-2`인 행이 DB에 없으면 `GetFileById(-1)`이 null을 반환해 다음 반복에서 죽는다(`GetFileById`는 소유자 필터 없이 `file_id`만 보므로 계정별이 아니라 시스템 전체에 이 행이 하나만 있으면 됨). 지금까지 이 행은 운영 DB에 수동으로 넣어둔 상태였고 스키마/시드 스크립트 어디에도 없었다 — 새 DB(Phase 5 재배포 등)로 옮기면 아무도 모르게 다시 깨질 수 있었음. `Api/OverCloud.Api/Program.cs`에 API 서버 시작 시 이 행이 없으면 자동 생성하는 부트스트랩을 추가함(기존 값 `ID='DEFAULT', file_name='ROOT'`과 동일하게 맞춰서 idempotent — 이미 행이 있는 운영 DB엔 영향 없음).
- 기존에 노출됐던 root 비밀번호와 OAuth 앱 시크릿은 재배포 시 **반드시 재발급/로테이션**해야 한다 (git 이력에 남아있을 수 있으므로 유출된 것으로 간주).
- API 서버 자체의 가용성이 곧 서비스 가용성이 됨 — 기존에는 DB만 죽으면 됐지만 이제 API 서버도 단일 장애점이 됨.
- 오프라인/네트워크 불안정 시나리오에 대한 클라이언트 동작(캐시, 재시도)을 새로 고려해야 함.
- **LAN 전송 signaling 회귀**: `LanTransferService`가 피어 IP/온라인 상태를 DB에서 직접 조회·갱신하고 있어(5.6 참고), `presence` API를 Phase 4와 동시에 만들지 않으면 클라이언트 DB 직접 접속을 끊는 순간 LAN 전송이 깨진다.
- **JWT는 즉시 무효화가 안 됨**: 정지/정책 변경을 즉시 반영하려면 authorization 판단을 토큰이 아니라 매 요청 시 DB 조회로 해야 한다 (5.5 참고). 이를 놓치면 "정지시켰는데 access token 만료 전까지 계속 쓸 수 있는" 창구가 생긴다.
- **공격 표면이 없어진 게 아니라 이동함**: 지금은 MySQL 3306 포트만 막으면 됐지만, 전환 후엔 API 서버가 공개적으로 열려있어야 하므로 인증 우회·요청 위조·무차별 대입에 대한 방어(rate limiting, 입력 검증, IDOR 점검)가 새로 필요하다 (Phase 6에 반영).
- **Phase 4는 빅뱅 배포**: 클라이언트가 DB 직접 접속 코드를 걷어내는 순간부터 신규 API 서버 없이는 클라이언트가 동작하지 않는다. 롤백 계획(구버전 exe 보관, 구 DB 스냅샷 보존 기간) 없이 이 Phase를 배포하지 말 것 (Phase 5 참고).

## 9. 다음 결정 필요 사항

- 무료/저비용으로 MySQL 호스팅이 가능한 클라우드 조사 및 선정 (별도 조사 예정)
- API 서버 호스팅 방식(같은 VM에 함께 둘지, 별도 컨테이너/PaaS로 분리할지)
