﻿using Raven.Studio.Features.Stats;
using Raven.Studio.Infrastructure;

namespace Raven.Studio.Models
{
	public class StatusPageModel: PageViewModel
    {
		public StatusPageModel()
        {
            Status = new StatusModel();
			ModelUrl = "/Status";
        }

        public string CurrentDatabase
        {
            get { return ApplicationModel.Current.Server.Value.SelectedDatabase.Value.Name; }
        }

	    protected override void OnViewLoaded()
	    {
		    Status.Sections.Add(new StatisticsStatusSectionModel());
			Status.Sections.Add(new LogsStatusSectionModel());
			Status.Sections.Add(new AlertsStatusSectionModel());
			Status.Sections.Add(new IndexesErrorsStatusSectionModel());
			Status.Sections.Add(new ReplicationStatisticsStatusSectionModel());
			Status.Sections.Add(new UserInfoStatusSectionModel());

			var url = new UrlParser(UrlUtil.Url);

			var id = url.GetQueryParam("id");
			if (string.IsNullOrWhiteSpace(id) == false)
			{
				switch (id)
				{
					case "indexes-errors":
						Status.SelectedSection.Value = Status.Sections[3];
						break;
					case "replication":
						Status.SelectedSection.Value = Status.Sections[4];
						break;
					default:
						Status.SelectedSection.Value = Status.Sections[0];
						break;
				}
			}
			else
				Status.SelectedSection.Value = Status.Sections[0];
	    }

	    public StatusModel Status { get; private set; }
    }
}
