using System;
using System.Collections.Generic;

namespace Vivian.Networking.Contracts
{
    [Serializable]
    public class JobCreateRequest
    {
        public string scene_json_path;
        public string views_manifest_path;
        public string views_dir;
        public string output_dir;
        public List<string> object_names;
        public bool start_pipeline;
        public bool only_scene_analysis;
        public bool use_mock_scene_analysis;
    }

    [Serializable]
    public class JobResponse
    {
        public string job_id;
        public string status;
        public string created_at;
        public string started_at;
        public string finished_at;
        public float progress;
        public string stage;
        public string error;
        public string result_path;
    }

    [Serializable]
    public class JobResultResponse
    {
        public string job_id;
        public string status;
        public string result_path;
    }

    [Serializable]
    public class HealthResponse
    {
        public string status;
        public string version;
        public string run_id;
    }

    [Serializable]
    public class InfoResponse
    {
        public string name;
        public string version;
        public string run_id;
        public string started_at;
        public string host;
        public int port;
        public List<string> endpoints;
    }
}
