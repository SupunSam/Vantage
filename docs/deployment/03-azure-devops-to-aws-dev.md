# Guide 3: Azure DevOps repository, CI/CD to the AWS Dev environment, and the infrastructure it needs

Audience: the person who will set up the repository, the pipeline and the AWS Dev environment.

> **Read section 1 first.** This is a plan and a draft. Nothing in it has been built or run: the pipeline YAML in
> section 6 is not in the repository, and the AWS resources do not exist yet. Two parts of the application are not
> ready to run on AWS yet, and the approved costing leaves out a database and file storage.

## 1. Where things stand (important)

### 1.1 The application cannot start outside the Development environment yet

| Gap in the code today | Effect on AWS | Planned fix |
| --- | --- | --- |
| `Program.cs` throws `Only the Development environment is configured in this build` in any other environment | The API container will not start with `ASPNETCORE_ENVIRONMENT=Production` | Build real sign-in (ADFS SAML federated into Cognito for @rrd.com; Cognito with authenticator-app MFA for externals), refuse people who are not already users, separate session per portal |
| Database migration and seeding (`PrepareDatabaseAsync`) run only in Development | No tables, roles or settings would be created in AWS | Run migrations as a pipeline step (migration bundle) and keep seeding safe to run at deploy |
| Uploaded files use `LocalFileStore` only (`IFileStore` has no S3 version yet) | Files would sit on one instance's disk | Add an S3 `IFileStore` |
| Data Protection keys are files in `DataProtection:KeysPath` | Lost if the instance is replaced, and stored tenant secrets become unreadable | Durable key store (for example S3 plus KMS, or a database-backed key ring), keeping `SetApplicationName("Vantage")` |

**Interim option, needs the project owner's explicit decision:** running the AWS Dev API with
`ASPNETCORE_ENVIRONMENT=Development` would work today but switches on the sign-in picker that lets anyone choose to
be any user. It is only acceptable if the environment is reachable solely from an approved network (IP allow-list
or VPN) and holds no real data. We do not recommend it by default.

Because of this, the pipeline in this guide can be built and used now to **build, test and publish images**; the
**deploy** stage will only produce a working Dev site after the items above are done.

### 1.2 What the approved Dev costing covers, and what it leaves out

Approved sheet (Dev, Mumbai region, `ap-south-1`): about **$260.45 per month, $3,125.35 per year**.

| # | Service | Qty/size on the sheet | Monthly | Annual | How Vantage uses it |
| --- | --- | --- | --- | --- | --- |
| 1 | Cognito | users | $16.00 | $192.05 | Sign-in for external users and the ADFS federation |
| 2 | ECS | one `t3.xlarge` (4 vCPU, 16 GB), "2 web apps and 1 web API" | $160.69 | $1,928.33 | The container host: Admin Portal, User Portal and API containers. A `t3.xlarge` means ECS on EC2, not Fargate |
| 3 | Secrets Manager | 2 secrets | $2.66 | $31.93 | For example the database connection string and the email credentials |
| 4 | ECR | 3 repositories | $5.78 | $69.31 | One image repository each for API, Admin Portal, User Portal |
| 5 | SES | email | $10.81 | $129.75 | Outgoing email (notifications, approvals) |
| 6 | Inspector | 3 | $5.76 | $69.16 | Image and instance vulnerability scanning |
| 7 | ALB and data transfer | 30 GB | $21.05 | $252.64 | Entry point and routing |
| 8 | VPC data traffic | 30 GB | $10.64 | $127.72 | Network |
| 9 | CloudFront | 30 GB | $11.03 | $132.40 | CDN in front |
| 10 | CloudWatch | 3 | $16.00 | $192.05 | Logs and alarms |

**Not on the approved sheet but needed.** Each of these needs a decision or a costing addition before go-live:

| Missing | Why Vantage needs it | Options |
| --- | --- | --- |
| **SQL Server database** | The application stores everything in SQL Server (`Vantage`) | RDS for SQL Server (requirements say RDS; Multi-AZ is for Prod), or SQL Server on the EC2 host (adds licence and patching). Dev can use a single-AZ RDS instance. Name `Vantage` and login `vantage_app` must be kept |
| **S3 bucket** | The requirements put uploaded files, thumbnails and the last 3 file versions in S3 | One private, encrypted bucket per environment (needs the S3 code in 1.1) |
| **Disk for keys and files (interim)** | Until the S3 and key changes exist, `/data` must survive container restarts | An EBS volume mounted on the instance |
| **GenAI origin** | GenAI dashboards must be served from a different registrable domain from the portals | A fourth small nginx service (config `deploy/genai.nginx.conf`) behind the same ALB on its own host name, plus a second domain |
| **DNS and certificates** | Domain names for the two portals and the GenAI origin, with HTTPS | Route 53 (or RRD's DNS) and ACM certificates (ACM public certificates are free) |
| **Outbound internet** | The API calls Power BI, Azure AD and Tableau | Put the instance in a public subnet with a public IP and a security group that allows inbound traffic only from the ALB. A NAT gateway (not costed) is needed if you use private subnets |
| **Malware scanner (optional)** | GenAI upload scanning with ClamAV | A fifth service, only if the owner wants scanning in Dev |

The proposed costing that adds these items is in section 9.

All 3 ECR repositories are enough for API, Admin Portal and User Portal. The GenAI nginx can use the public
`nginx` image or a fourth repository.

## 2. Target picture for Dev

```
 Users --> CloudFront --> ALB (HTTPS, host-based rules) --> ECS service "admin" (nginx + Admin Portal)
                                  |                    --> ECS service "user"  (nginx + User Portal)
                                  |                    --> ECS service "genai" (nginx, other domain)
                                  |
   admin and user nginx forward /api/  ----------------->  ECS service "api" (ASP.NET Core, port 8080)
                                                                 |-- SQL Server on the host (Vantage; RDS in UAT/Prod)
                                                                 |-- S3 bucket (files)
                                                                 |-- Secrets Manager, SES, Cognito
   Azure DevOps pipeline --> ECR (3 repos) --> ECS (new task definition revision)
```

Each service runs on one ECS cluster backed by one EC2 instance (`t3.xlarge`, ECS-optimised Amazon Linux AMI, in an
Auto Scaling group of size 1). Use **ECS Service Connect** (or Cloud Map) so the portals' nginx can reach the API
by the name `api` on port 8080; then `deploy/nginx.conf` (`proxy_pass http://api:8080`) works unchanged.

Settings the API needs (names are from the code and compose file; values come from Secrets Manager or the task
definition, never from the repository):

| Setting | Source |
| --- | --- |
| `ConnectionStrings__Default` | Secrets Manager. Database `Vantage`, login `vantage_app`, encrypted connection |
| `ASPNETCORE_URLS` | Image default `http://+:8080` |
| `DataProtection__KeysPath` | Interim: a mounted EBS path such as `/data/keys`. Final: durable key store (1.1) |
| `Storage__LocalPath` | Interim: `/data/files`. Final: S3 (1.1) |
| `Bootstrap__SuperAdminEmail` | Task definition (not secret) |
| `Email__SmtpHost`, `Email__SmtpPort`, `Email__UseSsl`, `Email__UserName`, `Email__Password`, `Email__FromAddress`, `Email__UserPortalUrl`, `Email__AdminPortalUrl` | SES SMTP endpoint for `ap-south-1`, credentials in Secrets Manager; the URLs are the real portal addresses |
| `GenAi__BaseUrl`, `GenAi__FrameAncestors` | The GenAI domain and the two portal addresses |
| `Jobs__Enabled` | `true` on one API instance. Scheduled runs are claimed atomically, so more instances are safe |

SES starts in the **sandbox** (it can only send to verified addresses). Verify the sender domain (DKIM) and request
production access before real users get email. SES SMTP credentials are different from IAM keys.

## 3. Prerequisites and who does what

| Who | Task |
| --- | --- |
| AWS account owner | Create or give access to the Dev account; create the IAM roles in section 5; approve the missing items in 1.2 |
| Azure DevOps admin | Create the organisation/project (or use an existing one); request free parallel jobs if needed (below); install the AWS Toolkit extension |
| DNS/certificate owner | Domain names, ACM certificates, validation records |
| Project owner | Decide the interim option in 1.1 and the database/S3 choices |

**Free pipeline minutes:** new Azure DevOps organisations must request a free grant of Microsoft-hosted parallel
jobs (a short form; approval can take a few working days). Otherwise use a self-hosted agent (a Linux machine with
Docker and the AWS CLI) or purchase a parallel job.

## 4. Part A: Move the code to Azure DevOps

The code lives on GitHub (`SupunSam/Vantage`). **Decision C53 (6 Oct 2026): Azure DevOps replaces GitHub** as the home of the code.

What this means for the repository (to be done at the move, not before):
- `.github/workflows/ci.yml` (build, SQL tests, frontend checks, per-area skipping) is rebuilt as an Azure Pipelines CI pipeline.
- The GitHub branch ruleset on `main` is replaced by Azure DevOps branch policies (step 5): pull request required, the two CI jobs as build validation.
- The `.claude` SessionStart hook and the cloud coding sessions are tied to GitHub today; working with Claude on the Azure repo needs its own set-up, so keep GitHub as the working remote until that is agreed.
- Until the move is done, GitHub stays the working home and `main` there stays protected.

1. In Azure DevOps, create a project (for example `Vantage`), then **Repos, Files**, and create an **empty** repo
   named `Vantage` (do not initialise it with a README).
2. Copy the clone URL it shows (for example `https://dev.azure.com/<org>/Vantage/_git/Vantage`).
3. In the **local clone** of the repo (Guide 1, section 3), add Azure as a second remote and push all branches and tags:

   ```powershell
   cd C:\Code\Vantage
   git remote add azure https://dev.azure.com/<org>/Vantage/_git/Vantage
   git push azure --all
   git push azure --tags
   ```
   Sign in when prompted (Git Credential Manager opens a browser). If it asks for a password, use a Personal Access
   Token created at Azure DevOps, User settings, Personal access tokens, scope **Code (Read & write)**. Keep the
   token private; never paste it into chat or commit it.
4. Decide the source of truth:
   - **Azure DevOps becomes the main repo:** make it the default remote (`git remote rename origin github`,
     `git remote rename azure origin`), and stop pushing to GitHub. The GitHub Actions file
     `.github/workflows/ci.yml` stops being useful; the pipeline below replaces it.
   - **Both for a while:** push to both; keep one as primary to avoid divergence.
5. Protect `main`: **Repos, Branches, `main`, Branch policies**: require a pull request, at least one reviewer, linked
   work items (optional), and a **Build validation** policy pointing at the CI pipeline from section 6 (after you
   create it).
6. Check that `deploy\.env` and `deploy\certs\*` did not get pushed (they are git-ignored, so they should not).

## 5. Part B: AWS side (Dev account, `ap-south-1`)

Create these once. Prefer infrastructure as code (CloudFormation, CDK or Terraform) so Dev, UAT and Prod match; the
requirements ask for it. The list below is what the code has to be written for.

1. **Network:** one VPC with two public subnets in different Availability Zones; Internet gateway. (RDS needs a DB
   subnet group across two AZs even if Dev is single-AZ.)
2. **Security groups:**
   - ALB: inbound 443 from the allowed networks (Dev: restrict to RRD ranges); outbound to the instance.
   - ECS instance: inbound only from the ALB's group on the container ports; no SSH from the internet (use SSM
     Session Manager).
   - Database: inbound 1433 only from the ECS instance group.
3. **ECR:** three private repositories: `vantage-api`, `vantage-admin`, `vantage-user`. Turn on scan on push and
   immutable tags (Inspector is on the approved sheet).
4. **ECS cluster** `vantage-dev` with an EC2 capacity provider (ASG of 1, `t3.xlarge`), then services `api`,
   `admin`, `user` (and `genai` once approved). Task definitions use `awsvpc` or `bridge` networking with Service
   Connect; log driver `awslogs` into CloudWatch log groups per service.
5. **ALB:** HTTPS listener (ACM certificate), host-based rules to each service's target group, health check path
   `/api/health` for the API and `/` for the portals. **Raise the idle timeout** from 60 s to about 600 s: .pbix
   uploads can be up to 1 GB and the nginx config allows 600 s.
6. **CloudFront:** in front of the ALB. Large uploads (the publish `.pbix` endpoint) can run longer than CloudFront's
   origin response timeout, so test uploads through it; if they time out, route the API upload path directly to the
   ALB or raise the CloudFront origin timeout quota.
7. **Secrets Manager:** two secrets (as costed): the database connection string, and the SES SMTP credentials.
   Never put values into the repository, pipeline YAML or chat. The ECS task execution role reads them.
8. **IAM roles:**
   - ECS **task execution role:** pull from ECR, write logs, read the two secrets.
   - ECS **task role** (the API's own permissions): later, S3 access to the bucket, and anything else the API calls.
   - ECS **instance profile:** the standard ECS container-instance policy plus SSM.
   - **Pipeline identity** (used by Azure DevOps): permissions to log in to ECR and push to the three repositories,
     `ecs:RegisterTaskDefinition`, `ecs:DescribeTaskDefinition`, `ecs:UpdateService`, `ecs:DescribeServices`,
     `ecs:RunTask`, and `iam:PassRole` for the two ECS roles. Restrict it to these resources. Prefer a role with
     short-lived credentials; if an IAM user with access keys is the only option, store the keys only inside the Azure
     DevOps service connection.
9. **RDS SQL Server (UAT and Prod only; Dev runs SQL Server on the host, C51):** subnet group, encryption at rest, automated backups (point-in-time restore is a requirement),
   parameter set, and the `Vantage` database with the `vantage_app` login (adapt `deploy/db/init.sql`: on RDS the
   `db_owner` grant for migrations is acceptable only in Dev; later give migrations a separate owner login and the
   running API a narrower one). The dummy HRMS rows in `init.sql` are for local use; in Dev the HRMS table is filled
   by the SQL-level sync run by RRD.
10. **Cognito:** a user pool for external users with MFA required (authenticator app), and the ADFS SAML identity
    provider for @rrd.com users with Duo enforced at ADFS. This needs ADFS metadata and domain details from the
    project owner. The app clients, one per portal, keep the sessions separate.
11. **SES:** verify the domain, set up DKIM, create SMTP credentials, ask for production access.
12. **CloudWatch:** log retention (for example 30 days in Dev), alarms for unhealthy targets, 5xx rate, CPU/memory
    and database storage, notifying IT Ops.
13. **S3 (once built):** private bucket, block public access, default encryption, versioning optional.

## 6. Part C: The pipeline

Create the following in Azure DevOps.

### 6.1 One-time setup

1. **Install the extension:** Organization settings, Extensions, Browse marketplace, **AWS Toolkit for Azure DevOps**.
2. **Service connection:** Project settings, Service connections, New, **AWS**, named `aws-vantage-dev` (the pipeline
   below uses this name). Use the pipeline identity from section 5. Keys, if used, are entered here only.
3. **Variable group** `vantage-dev` (Pipelines, Library): non-secret values `AWS_REGION=ap-south-1`,
   `AWS_ACCOUNT_ID=<12-digit id>`, `ECS_CLUSTER=vantage-dev`, `ECR_REGISTRY=<account>.dkr.ecr.ap-south-1.amazonaws.com`;
   mark `CI_SQL_PASSWORD` as **secret** (a throwaway password used only for the CI test database, 8+ characters with
   upper, lower, digit and symbol; it is not a real secret but should not be in git).
4. **Environment** `vantage-dev` (Pipelines, Environments): add an **Approval** check if you want a person to
   approve each Dev deployment.
5. **Pipeline:** Pipelines, New pipeline, Azure Repos Git, your repo, **Existing Azure Pipelines YAML file**, select
   `/azure-pipelines.yml` (add the file from 6.2 to the repo root first).
6. Add the CI pipeline as the **Build validation** policy on `main` (section 4, step 5).

### 6.2 Draft `azure-pipelines.yml` (not yet in the repo)

Task names and parameters should be checked against the installed AWS Toolkit extension version. This draft uses
plain scripts with the AWS CLI where possible (the Microsoft-hosted Ubuntu image includes Docker and the AWS CLI).

```yaml
trigger:
  branches:
    include: [ main ]

pool:
  vmImage: ubuntu-latest

variables:
  - group: vantage-dev
  - name: IMAGE_TAG
    value: $(Build.BuildId)

resources:
  containers:
    - container: sql
      image: mcr.microsoft.com/mssql/server:2022-latest
      ports: [ '1433:1433' ]
      env:
        ACCEPT_EULA: 'Y'
        MSSQL_PID: Developer
        MSSQL_SA_PASSWORD: $(CI_SQL_PASSWORD)

stages:
  - stage: Build
    displayName: Build and test
    jobs:
      - job: Backend
        services:
          sql: sql
        steps:
          - task: UseDotNet@2
            inputs: { packageType: sdk, version: 10.0.x }
          - script: dotnet build backend/Vantage.slnx -c Release
            displayName: Build
          - script: |
              for i in $(seq 1 30); do
                /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$CI_SQL_PASSWORD" -C -Q "SELECT 1" -b -o /dev/null && exit 0
                sleep 5
              done
              echo "SQL Server did not become ready" && exit 1
            displayName: Wait for SQL Server
            env: { CI_SQL_PASSWORD: $(CI_SQL_PASSWORD) }
          - script: dotnet test backend/Vantage.slnx -c Release --no-build --logger trx
            displayName: Test
            env:
              VANTAGE_TEST_SQL: Server=localhost,1433;User Id=sa;Password=$(CI_SQL_PASSWORD);TrustServerCertificate=True
          - task: PublishTestResults@2
            condition: succeededOrFailed()
            inputs: { testResultsFormat: VSTest, testResultsFiles: '**/*.trx' }
      - job: Frontend
        steps:
          - task: NodeTool@0
            inputs: { versionSpec: '22.x' }
          - script: cd frontend && npm ci --no-audit --no-fund && npm run typecheck && npm run build
            displayName: Install, typecheck, build

  - stage: Images
    displayName: Build and push images
    dependsOn: Build
    condition: and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/main'))
    jobs:
      - job: Push
        steps:
          - task: AWSShellScript@1
            displayName: Build and push the three images
            inputs:
              awsCredentials: aws-vantage-dev
              regionName: $(AWS_REGION)
              scriptType: inline
              inlineScript: |
                set -euo pipefail
                aws ecr get-login-password --region "$AWS_REGION" | docker login --username AWS --password-stdin "$ECR_REGISTRY"
                for svc in api admin user; do
                  docker build -f deploy/$svc.Dockerfile -t "$ECR_REGISTRY/vantage-$svc:$(IMAGE_TAG)" .
                  docker push "$ECR_REGISTRY/vantage-$svc:$(IMAGE_TAG)"
                done

  - stage: DeployDev
    displayName: Deploy to AWS Dev
    dependsOn: Images
    jobs:
      - deployment: Dev
        environment: vantage-dev          # approvals are configured on this environment
        strategy:
          runOnce:
            deploy:
              steps:
                - checkout: self
                # 1. Database migrations: see 6.3. Run before the API changes.
                - task: AWSShellScript@1
                  displayName: Roll out new task definitions
                  inputs:
                    awsCredentials: aws-vantage-dev
                    regionName: $(AWS_REGION)
                    scriptType: inline
                    inlineScript: |
                      set -euo pipefail
                      for svc in api admin user; do
                        FAMILY=vantage-dev-$svc
                        TD=$(aws ecs describe-task-definition --task-definition "$FAMILY" --query taskDefinition)
                        NEW=$(echo "$TD" | jq --arg img "$ECR_REGISTRY/vantage-$svc:$(IMAGE_TAG)" \
                          'del(.taskDefinitionArn,.revision,.status,.requiresAttributes,.compatibilities,.registeredAt,.registeredBy)
                           | .containerDefinitions[0].image = $img')
                        ARN=$(aws ecs register-task-definition --cli-input-json "$NEW" --query taskDefinition.taskDefinitionArn --output text)
                        aws ecs update-service --cluster "$ECS_CLUSTER" --service "$svc" --task-definition "$ARN"
                      done
                      aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services api admin user
```

What it does: every merge to `main` builds and tests (the same checks as the current GitHub CI, with SQL Server as a
service container), then builds the three images tagged with the build number, pushes them to ECR, and after an
optional approval registers a new task definition revision per service and waits until ECS reports them stable.
The task definitions themselves (environment variables, secrets, ports, log groups, CPU/memory) are created once by
your infrastructure code; the pipeline only swaps the image.

### 6.3 Database migrations on deploy (to be built)

Today migrations run when the API starts, and only in Development. For AWS the recommended approach is a one-off step
before the API rolls out:

1. In the Build stage run `dotnet ef migrations bundle` (produces a single executable) and publish it as a build
   artifact. (Local-tool restore: `dotnet tool restore`; the tool is pinned in `backend/dotnet-tools.json`.)
2. In the deploy stage, run the bundle once against the Dev database, either from the agent (needs network access to
   RDS, which sits in a VPC; use a self-hosted agent in the VPC or a one-off ECS task) or as an ECS task using the
   API image with the bundle.
3. Keep migrations backward compatible so the old API version still works while the new one starts.
4. Never rename existing migrations (their IDs are stored in `app.__EFMigrationsHistory`).

### 6.4 Rollback

Each deploy creates a new task definition revision, and old revisions and ECR images stay. To roll back, re-run
the update with the previous revision: `aws ecs update-service --cluster vantage-dev --service api --task-definition
vantage-dev-api:<previous revision>` (and the same for `admin`, `user`), or re-run an earlier pipeline run. Database
migrations are **not** undone by this; that is why they must stay backward compatible (6.3).

### 6.5 Promotion to UAT and Prod (later)

Reuse the same images: add stages `DeployUat` and `DeployProd` that deploy the **same image tag** to the other
accounts' clusters, each with its own environment and approvals, secrets and domains. Never rebuild for Prod.

## 7. Check it works (after the code in 1.1 exists)

1. Open the pipeline run: all stages green; the DeployDev stage waits for `services-stable`.
2. ECR shows the three images with the build number tag; Inspector shows scan results.
3. ECS, cluster `vantage-dev`: services `api`, `admin`, `user` at desired count with healthy targets.
4. `https://<dev domain>/api/health` returns `{"status":"ok","database":"ok"}`.
5. Sign in with a pre-created test user; a person who is not a user must be refused.
6. Send a test notification; confirm the email arrives (SES sandbox: to a verified address).
7. Upload a GenAI test file and open it; confirm it loads from the separate GenAI domain.
8. CloudWatch shows the logs and the alarms exist.

## 8. Decisions we need from the project owner

Answered on 6 Oct 2026 (decisions C50 to C53 in the requirements log):

1. **Dev sign-in.** Wait for real ADFS/Cognito sign-in. Nothing is deployed to AWS before it exists, and the temporary `Development` picker is not used (C50).
2. **SQL Server.** On the EC2 host in Dev (C51), RDS Standard Multi-AZ for UAT and Prod.
3. **Extra costing.** Approved, recomputed in section 9 (C52): S3, the database and `/data` volumes, public IPv4 addresses and the GenAI origin service. Grand total $287.58 a month.
4. **Domains, DNS and certificates.** RRD IT manages DNS and TLS certificates for the Admin Portal, User Portal and GenAI origin (C52). We send them the host names; the GenAI host must be a different registrable domain.
5. **Code home.** Azure DevOps replaces GitHub (C53). See the note under Part A about what must be rebuilt.
6. **Still open:** ADFS metadata, domains and whether a Cognito user pool already exists (needed for the sign-in work).

Open items are recorded as the next decision numbers.

## 9. Proposed AWS Dev costing for the current build

This extends the approved Dev sheet (rows 1 to 10, copied unchanged) with what the current build needs and the
approved sheet leaves out (rows 11 to 18, marked **Proposed**). Same columns as the approved sheet.

**How the new figures were worked out.** From AWS's own published price list files for the Mumbai region
(`ap-south-1`), downloaded on 4 Oct 2026: on-demand, USD, 730 hours a month, annual (ARC) = monthly x 12, before
tax and AWS support. Nothing is reserved or discounted. The approved rows are the sheet's numbers and were not
recalculated (for example, the list price of a `t3.xlarge` in Mumbai is $0.1792 an hour, the sheet says $0.17).
Sizes for the new rows are assumptions for a Dev environment and are stated in the comments.
Please confirm the final figures in the AWS Pricing Calculator before submitting for approval.

| S.no | Application | Environment | Region | Component Type | Instance Type | CPU | RAM | Qty | unit / per hour cost | Total cost / Month | ARC Total | OTC | Comments |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Cognito | Dev | Mumbai | Cognito User | NA | NA | NA | NA | NA | $16.00 | $192.05 | | Approved |
| 2 | ECS | Dev | Mumbai | Admin Portal & Dashboard Portal Webapp & webapi | t3.xlarge | 4 | 16 | 1 | $0.17 | $160.69 | $1,928.33 | | Approved. 2 Webapps & 1 Webapi |
| 3 | Secrets Manager | Dev | Mumbai | Secrets Store | NA | NA | NA | 2 | NA | $2.66 | $31.93 | | Approved |
| 4 | ECR | Dev | Mumbai | Elastic Container Registry | NA | NA | NA | 3 | NA | $5.78 | $69.31 | | Approved. API, Admin Portal, User Portal images |
| 5 | SES | Dev | Mumbai | Email Service | NA | NA | NA | NA | NA | $10.81 | $129.75 | | Approved |
| 6 | Inspector | Dev | Mumbai | Amazon Inspector | NA | NA | NA | 3 | NA | $5.76 | $69.16 | | Approved |
| 7 | ALB & Data Transfer | Dev | Mumbai | ALB/ALB Data transfer | NA | NA | NA | 30GB | NA | $21.05 | $252.64 | | Approved |
| 8 | VPC Data | Dev | Mumbai | VPC Data Traffic | NA | NA | NA | 30GB | NA | $10.64 | $127.72 | | Approved |
| 9 | Cloudfront | Dev | Mumbai | CDN Layer | NA | NA | NA | 30GB | NA | $11.03 | $132.40 | | Approved |
| 10 | Cloudwatch | Dev | Mumbai | Monitoring Service | NA | NA | NA | 3 | NA | $16.00 | $192.05 | | Approved |
| 11 | S3 | Dev | Mumbai | Uploaded files, thumbnails, last 3 file versions, and database backups | NA | NA | NA | 100GB | $0.025 per GB-month | $2.50 | $30.00 | | **Proposed.** Needs the S3 file store to be built. Request charges are a few cents |
| 12 | EBS | Dev | Mumbai | gp3 volume mounted on the ECS instance for `/data` (keys and files) | NA | NA | NA | 50GB | $0.0912 per GB-month | $4.56 | $54.72 | | **Proposed, temporary.** Needed until the S3 store and durable key store exist, then removed |
| 13 | EBS | Dev | Mumbai | gp3 volume for the SQL Server data and log files on the instance | NA | NA | NA | 100GB | $0.0912 per GB-month | $9.12 | $109.44 | | **Proposed.** Decision C51: SQL Server Developer edition runs on the EC2 host (free for non-production use). Backups go to S3 (row 11) |
| 14 | VPC | Dev | Mumbai | Public IPv4 addresses (1 for the instance, 2 for the ALB) | NA | NA | NA | 3 | $0.005 per hour | $10.95 | $131.40 | | **Proposed.** Used instead of a NAT gateway. May overlap with charges already inside rows 7 and 8; confirm in the calculator |
| 15 | ECS | Dev | Mumbai | GenAI origin (small nginx service on its own domain) | runs on the row 2 instance | 0 | 0 | 1 | NA | $0.00 | $0.00 | | **Proposed.** No extra instance; uses a small share of the t3.xlarge. DNS and TLS certificates are provided by RRD IT (C52), so no Route 53 or ACM rows |
| | | | | | | | | | **Total (approved rows 1 to 10)** | **$260.45** | **$3,125.35** | **$0.00** | Approved sheet total |
| | | | | | | | | | **Total (proposed rows 11 to 15)** | **$27.13** | **$325.56** | **$0.00** | New |
| | | | | | | | | | **Grand total** | **$287.58** | **$3,450.91** | **$0.00** | Approved plus proposed |

Arithmetic for the proposed rows: row 11 is 100 x 0.025; row 12 is 50 x 0.0912; row 13 is 100 x 0.0912; row 14 is
3 x 0.005 x 730. Earlier drafts priced RDS for SQL Server Express (about $73.71 a month) and Route 53; both were dropped (C51, C52).

### 9.1 Choices behind the new rows

- **Database on the instance (C51).** Dev runs SQL Server **Developer** edition on the ECS host, licensed free for
  non-production use, with its own gp3 volume and a scheduled backup to S3. It shares the `t3.xlarge`'s 16 GB with the
  three app containers, so check memory headroom (SQL Server needs about 2 GB at least; set its memory limit). UAT and
  Prod need a paid edition: RDS for SQL Server **Standard**, Multi-AZ (a `db.m5.large` single-AZ is $1.078 an hour, about
  $786.94 a month; Multi-AZ is roughly double, not priced here; confirm in the calculator). Because Dev then differs from
  Prod, migrations are first proven against RDS in UAT. The `Vantage` database name and `vantage_app` login stay the same.
- **No NAT gateway.** The ECS instance sits in a public subnet (security group allows only the ALB in), so the API
  can reach Power BI, Azure AD and Tableau. Private subnets with a NAT gateway would add about $40.88 a month
  ($0.056 an hour) plus $0.056 per GB processed, and more public IPv4 charges.
- **No extra instance for GenAI.** The GenAI origin is a small nginx container. If the owner wants the optional
  ClamAV malware scan in Dev, it needs about 2 GB of memory; check headroom on the `t3.xlarge` first.
- **Not costed:** KMS customer-managed keys ($1 a month each; AWS-managed keys are free and meet encryption at
  rest), Direct Connect or VPN, Secrets Manager beyond the two approved secrets ($0.40 per secret a month), AWS WAF,
  AWS Backup beyond the S3 database backups, support plan, taxes.

### 9.2 What changes for UAT and Prod (not costed here)

- The database moves off the instance to RDS **Standard edition, Multi-AZ**, with larger storage; this becomes the largest line.
- The API runs on at least two instances in two Availability Zones (the job runner already claims runs atomically),
  with the ALB spreading across them.
- SES leaves the sandbox; CloudWatch alarms notify IT Ops; a penetration test is booked before go-live.
- Prod would normally be priced separately, with reserved or savings-plan pricing for the instances and database.
