FROM python:3.12-slim
WORKDIR /app
COPY pyproject.toml .
COPY veeam_vhc_aws/ veeam_vhc_aws/
RUN pip install --no-cache-dir .
ENTRYPOINT ["veeam-vhc-aws"]
CMD ["all", "--config", "/config/config.yaml"]
