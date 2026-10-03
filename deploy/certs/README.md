# Company root certificates

If your network inspects HTTPS traffic (for example Zscaler or a corporate proxy), the API container can't reach
Microsoft sign-in or the Power BI API until it trusts your company's root certificate.

Put the root certificate here as a `.crt` file (PEM format), then rebuild:

    docker compose -f deploy/docker-compose.yml up -d --build

Leave this folder empty otherwise.
