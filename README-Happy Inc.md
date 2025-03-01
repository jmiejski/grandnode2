# GrandNode Fork for Happy Inc.

This repository is a fork of the [GrandNode](https://github.com/grandnode/grandnode2) project. <br>
It serves as the backend for Happy Inc.'s e-commerce platform, **Happy Price**. <br>
This customized version of GrandNode will be tailored to meet the specific needs of Happy Price.

## Purpose

- **Backend Development**: Customize and extend GrandNode functionalities to support Happy Price's requirements.
- **Upstream Synchronization**: Regularly incorporate updates from the original GrandNode repository to ensure compatibility and access to new features.

## Getting Started

To set up this project locally and maintain synchronization with the upstream repository, follow these steps:

### Prerequisites

- Ensure you have [Git](https://git-scm.com/) installed on your machine.

### Installation

1. **Clone Your Forked Repository**:

   ```bash
   git clone https://github.com/your-username/grandnode2.git

2. **Navigate to the Project Directory**:
    ```bash
    cd grandnode2

3. **Add the Upstream Remote**
    ```bash
    git remote add upstream https://github.com/grandnode/grandnode2.git

4. **Verify Remote Repositories**

    ```bash
    git remote -v

### Keeping Your Fork Updated

1. **Fetch Upstream Changes**:

   ```bash
   git fetch upstream

2. **Merge Upstream Changes into Your Local Branch**:

    ```bash
    git checkout main
    git merge upstream/main

3. **Push Merged Changes to Your Fork**:

    ```bash
    git push origin main