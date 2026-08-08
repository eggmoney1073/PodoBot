# PodoBot 배포 설정

목표는 개발자가 빌드 도구를 사용자 PC에 설치시키지 않는 것입니다.

## 1. GitHub 저장소

이 프로젝트를 GitHub 저장소에 올립니다.

개인용 PodoBot이라면 비공개 저장소를 권장합니다.

## 2. 치지직 개발자 앱

치지직 개발자 센터에서 앱을 만들고 다음 Redirect URI를 등록합니다.

```text
http://localhost:18766/auth/callback
```

PodoBot에 필요한 권한은 다음 기능 범위입니다.

```text
유저 정보 조회
채팅 메시지 조회
채팅 메시지 전송
```

## 3. GitHub Secrets

GitHub 저장소에서:

```text
Settings
→ Secrets and variables
→ Actions
→ New repository secret
```

으로 이동해 아래 두 값을 추가합니다.

```text
CHZZK_CLIENT_ID
CHZZK_CLIENT_SECRET
```

이 값들은 GitHub Actions의 Windows 빌드에서만 소스에 주입됩니다.

일반 PodoBot UI에는 Client ID / Client Secret 입력 화면이 없습니다.

주의: 데스크톱 앱에 포함된 Client Secret은 역공학으로 추출할 가능성을 완전히 막을 수 없습니다.
이 프로젝트는 한 사람이 사용하는 개인용 앱을 전제로 합니다.
공개 서비스로 배포할 경우 인증 구조를 서버 기반으로 바꾸는 편이 안전합니다.

## 4. 설치파일 생성

GitHub 저장소의:

```text
Actions
→ Build PodoBot Setup
→ Run workflow
```

를 누릅니다.

GitHub의 Windows 빌드 머신이 자동으로:

```text
.NET 8 준비
→ 치지직 설정 주입
→ PodoBot self-contained 빌드
→ Inno Setup 설치
→ PodoBotSetup.exe 생성
```

합니다.

완료 후 Workflow의 Artifacts에서:

```text
PodoBotSetup
```

을 내려받으면 됩니다.

그 안에 최종 사용자용:

```text
PodoBotSetup.exe
```

가 있습니다.

## 5. GitHub Release 자동 생성

버전 태그를 Push하면 자동으로 Release에도 설치파일을 올립니다.

예:

```bash
git tag v0.3.0
git push origin v0.3.0
```

완료되면 해당 GitHub Release에:

```text
PodoBotSetup.exe
```

가 첨부됩니다.

## 6. Smart App Control / SmartScreen

코드 서명 인증서가 있다면 아래 GitHub Secrets를 추가할 수 있습니다.

```text
WINDOWS_CERTIFICATE_BASE64
WINDOWS_CERTIFICATE_PASSWORD
```

그러면 Workflow가 PodoBot.exe와 PodoBotSetup.exe에 자동으로 서명합니다.

인증서가 없으면 해당 단계는 자동으로 건너뜁니다.

코드 서명이 없거나 평판이 쌓이지 않은 앱은 Windows에서 보안 경고가 표시될 수 있습니다.
